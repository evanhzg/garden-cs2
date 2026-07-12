using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRankingsCore;
using GardenRankingsCore.Config;
using GardenRankingsCore.Db;
using GardenRankingsCore.Managers;
using GardenRankingsCore.Rating;

namespace GardenRankings;

/// <summary>
/// Game-mode layer: cvar profiles per mode, classic per-round scramble,
/// Competitive Retakes (CR) matches, clutch rounds and instant map changes.
/// </summary>
public partial class RankingsModule
{
    private readonly CompetitiveMatchManager _cr = new();
    private readonly ClutchScheduler _clutch = new();
    private readonly Random _modesRandom = new();

    // R12: CR setup (team-pick warmup) / stop-confirm state. The old unanimity
    // vote is gone: /cr opens a paused warmup to arrange teams, /cr starts.
    private bool _crSetupActive;
    private ulong _crSetupInitiator;
    private ulong _crStopInitiator;
    private DateTime _crStopDeadlineUtc;
    private DateTime _crMatchStartedUtc;

    // Pending clutch round layout, applied at next round poststart (Pre).
    private Dictionary<ulong, CsTeam>? _pendingClutchTeams;
    private string? _pendingClutchAnnouncement;

    #region Mode cvars & scramble

    /// <summary>
    /// Applies the cvar profile of the current mode (Classic / Ranked / Competitive).
    /// </summary>
    private void ApplyModeCvars()
    {
        var cfg = Configs.GetConfigData().ModeCvars;
        foreach (var command in cfg.CommonCommands)
        {
            Server.ExecuteCommand(command);
        }

        var modeCommands = _cr.IsLive
            ? cfg.CompetitiveCommands
            : _ranked.IsActive
                ? cfg.RankedCommands
                : cfg.ClassicCommands;
        foreach (var command in modeCommands)
        {
            Server.ExecuteCommand(command);
        }
    }

    private void OnMapStartModes()
    {
        _clutch.OnMapStart();
        _pendingClutchTeams = null;
        _pendingClutchAnnouncement = null;
        _crSetupActive = false;
        _plugin.SuspendTeamManagement = false;

        // A live CR match cannot survive a map change.
        if (_cr.IsLive)
        {
            _cr.Cancel();
        }

        _plugin.AddTimer(1.0f, ApplyModeCvars);
    }

    private void ScrambleTeamsForNextRound()
    {
        var players = Helpers.GetTeamHumanPlayers();
        if (players.Count < 2)
        {
            return;
        }

        var tCount = players.Count(p => p.Team == CsTeam.Terrorist);
        var shuffled = players.OrderBy(_ => _modesRandom.Next()).ToList();

        for (var i = 0; i < shuffled.Count; i++)
        {
            var target = i < tCount ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
            if (shuffled[i].Team != target)
            {
                shuffled[i].SwitchTeam(target);
            }
        }
    }

    #endregion

    #region Round-end mode dispatch

    /// <summary>
    /// Runs at every non-warmup round end: CR bookkeeping, clutch scheduling
    /// and the classic scramble.
    /// </summary>
    private void HandleModesRoundEnd(int winnerTeamNum)
    {
        if (_cr.IsLive)
        {
            HandleCrRoundEnd(winnerTeamNum);
            return;
        }

        if (_ranked.IsActive)
        {
            return;
        }

        // Classic mode: clutch scheduling first; scramble only when no clutch is set up.
        _clutch.OnRoundPlayed();
        var teamHumans = Helpers.GetTeamHumanPlayers();

        if (_clutch.ShouldTriggerClutch(teamHumans.Count))
        {
            PrepareClutchRound(teamHumans);
        }
        else if (Configs.GetConfigData().ModeCvars.ScrambleTeamsEachClassicRound && !_crSetupActive)
        {
            // Never shuffle the sides mid CR-setup: players are arranging teams.
            _plugin.AddTimer(0.2f, ScrambleTeamsForNextRound);
        }
    }

    #endregion

    #region Clutch rounds

    private void PrepareClutchRound(List<CCSPlayerController> teamHumans)
    {
        var layout = _clutch.PickLayout(teamHumans.Count);
        var clutchSide = _modesRandom.Next(2) == 0 ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        var enemySide = clutchSide == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;

        var shuffled = teamHumans.OrderBy(_ => _modesRandom.Next()).ToList();
        var assignments = new Dictionary<ulong, CsTeam>();
        var clutcherNames = new List<string>();

        for (var i = 0; i < shuffled.Count; i++)
        {
            var steamId = Helpers.GetSteamId(shuffled[i]);
            if (steamId == 0)
            {
                continue;
            }

            if (i < layout.ClutcherCount)
            {
                assignments[steamId] = clutchSide;
                clutcherNames.Add(shuffled[i].PlayerName);
            }
            else
            {
                assignments[steamId] = enemySide;
            }
        }

        _pendingClutchTeams = assignments;
        _pendingClutchAnnouncement = Translator.Instance[
            "clutch.announce",
            string.Join(", ", clutcherNames),
            layout.ClutcherCount,
            layout.EnemyCount,
            clutchSide == CsTeam.Terrorist
                ? Translator.Instance["teams.terrorist_short"]
                : Translator.Instance["teams.counter_terrorist_short"]];
        _clutch.RegisterClutchRound();
    }

    /// <summary>
    /// Team enforcement for clutch rounds and CR.
    ///
    /// IMPORTANT — hook choice: the retakes core rebalances teams to its
    /// terrorist ratio in a Post hook on round_prestart, and Post-hook ordering
    /// on the SAME event is registration-order dependent. round_poststart PRE
    /// is deterministic: it fires after every round_prestart handler (the
    /// balancer) and before the Post round_poststart handler that allocates
    /// spawns — so spawns are assigned with our teams already applied.
    /// (Registered in RankingsModule.Load with HookMode.Pre.)
    /// </summary>
    public HookResult OnRoundPoststartPre(EventRoundPoststart @event, GameEventInfo info)
    {
        if (@event == null!)
        {
            return HookResult.Continue;
        }

        // CR: make sure roster players sit on the correct side each round.
        if (_cr.IsLive)
        {
            EnforceCrSides();
            return HookResult.Continue;
        }

        if (_pendingClutchTeams is null)
        {
            return HookResult.Continue;
        }

        foreach (var player in Helpers.GetTeamHumanPlayers())
        {
            var steamId = Helpers.GetSteamId(player);
            if (steamId != 0 &&
                _pendingClutchTeams.TryGetValue(steamId, out var team) &&
                player.Team != team)
            {
                player.SwitchTeam(team);
            }
        }

        return HookResult.Continue;
    }

    private void AnnounceClutchRoundIfPending()
    {
        if (_pendingClutchAnnouncement is not null)
        {
            Helpers.PrintToAll(_pendingClutchAnnouncement);
        }

        _pendingClutchAnnouncement = null;
        _pendingClutchTeams = null;
    }

    #endregion

    #region Competitive Retakes

    public void OnCrCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.IsHumanPlayer(player) || !_dbReady)
        {
            return;
        }

        var steamId = Helpers.GetSteamId(player);

        // Live match: participants can request a cancel with a confirmation.
        if (_cr.IsLive)
        {
            if (_cr.RosterOf(steamId) is null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (_crStopInitiator == steamId && now <= _crStopDeadlineUtc)
            {
                CancelCrMatch("cancelled");
                return;
            }

            _crStopInitiator = steamId;
            _crStopDeadlineUtc = now.AddSeconds(Configs.GetConfigData().Competitive.StopConfirmSeconds);
            Helpers.WriteNewlineDelimited(
                Translator.Instance["cr.stop_confirm_prompt",
                    (int) Configs.GetConfigData().Competitive.StopConfirmSeconds],
                player!.PrintToChat);
            return;
        }

        // R12: setup phase — /cr cancel aborts, /cr (with valid sides) starts.
        if (_crSetupActive)
        {
            if (commandInfo.GetArg(1).Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                EndCrSetup(announceCancel: true);
                return;
            }

            TryStartFromSetup(player!);
            return;
        }

        // R12: starting CR auto-disables Ranked Retakes.
        if (_ranked.IsActive)
        {
            _testBypassMinPlayers = false;
            _ranked.ForceDeactivate();
            Helpers.PrintToAll(Translator.Instance["cr.ranked_auto_disabled"]);
        }

        // R12: open the team-pick warmup instead of a unanimity vote.
        _crSetupActive = true;
        _crSetupInitiator = steamId;
        _plugin.SuspendTeamManagement = true;
        Server.ExecuteCommand("mp_warmup_pausetimer 1");
        Server.ExecuteCommand("mp_warmuptime 999999");
        Server.ExecuteCommand("mp_warmup_start");

        var allowedSizes = Configs.GetConfigData().Competitive.AllowedTeamSizes;
        Helpers.PrintToAll(Translator.Instance["cr.setup_started",
            player!.PlayerName,
            string.Join(" or ", allowedSizes.Select(s => $"{s}v{s}"))]);
    }

    /// <summary>Validates the arranged sides and launches the match.</summary>
    private void TryStartFromSetup(CCSPlayerController player)
    {
        var teamHumans = Helpers.GetTeamHumanPlayers();
        var tPlayers = teamHumans.Where(p => p.Team == CsTeam.Terrorist).ToList();
        var ctPlayers = teamHumans.Where(p => p.Team == CsTeam.CounterTerrorist).ToList();
        var allowedSizes = Configs.GetConfigData().Competitive.AllowedTeamSizes;

        if (tPlayers.Count != ctPlayers.Count || !allowedSizes.Contains(tPlayers.Count))
        {
            Helpers.WriteNewlineDelimited(
                Translator.Instance["cr.invalid_team_sizes",
                    string.Join(" or ", allowedSizes.Select(s => $"{s}v{s}"))],
                player.PrintToChat);
            return;
        }

        _crSetupActive = false;
        StartCrMatch();
    }

    /// <summary>Aborts the setup warmup and restores normal play.</summary>
    private void EndCrSetup(bool announceCancel)
    {
        _crSetupActive = false;
        _plugin.SuspendTeamManagement = false;
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_warmup_end");
        if (announceCancel)
        {
            Helpers.PrintToAll(Translator.Instance["cr.setup_cancelled"]);
        }
    }

    private void HandleCrTick()
    {
        var now = DateTime.UtcNow;
        if (_cr.IsLive && _crStopInitiator != 0 && now > _crStopDeadlineUtc)
        {
            _crStopInitiator = 0;
        }
    }

    private void StartCrMatch()
    {
        var teamHumans = Helpers.GetTeamHumanPlayers();
        var tPlayers = teamHumans.Where(p => p.Team == CsTeam.Terrorist).ToList();
        var ctPlayers = teamHumans.Where(p => p.Team == CsTeam.CounterTerrorist).ToList();
        var allowedSizes = Configs.GetConfigData().Competitive.AllowedTeamSizes;

        if (tPlayers.Count != ctPlayers.Count || !allowedSizes.Contains(tPlayers.Count))
        {
            Helpers.PrintToAll(Translator.Instance["cr.vote_failed"]);
            return;
        }

        _cr.StartMatch(
            tPlayers.Select(Helpers.GetSteamId).Where(id => id != 0).ToList(),
            ctPlayers.Select(Helpers.GetSteamId).Where(id => id != 0).ToList(),
            string.Join(" & ", tPlayers.Select(p => p.PlayerName)),
            string.Join(" & ", ctPlayers.Select(p => p.PlayerName))
        );
        _crMatchStartedUtc = DateTime.UtcNow;
        _crStopInitiator = 0;

        // R12: CR owns the sides for the whole match.
        _plugin.SuspendTeamManagement = true;

        ApplyModeCvars();
        PushNextCrRoundPlan();

        Helpers.PrintToAll(Translator.Instance["cr.match_started",
            _cr.TeamAName, _cr.TeamBName, Configs.GetConfigData().Competitive.RoundsPerHalf]);

        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_warmup_end");
        Server.ExecuteCommand("mp_restartgame 1");
    }

    /// <summary>
    /// Tells the allocator what the next CR round should be (pistol / full buy /
    /// force-buy for the losing streak team).
    /// </summary>
    private void PushNextCrRoundPlan()
    {
        var api = GetAllocatorApi();
        if (api is null)
        {
            Log.Warn("Allocator API unavailable: CR round types cannot be enforced.");
            return;
        }

        var plan = _cr.PlanNextRound();
        api.SetNextRoundTypeOverride(plan.RoundTypeOrdinal);
        api.SetNextForceBuyTeam(plan.ForceBuyTeamNum);
    }

    private void HandleCrRoundEnd(int winnerTeamNum)
    {
        var matchEvent = _cr.RoundCompleted(winnerTeamNum);

        if (matchEvent is CrMatchEvent.None or CrMatchEvent.HalftimeReached)
        {
            Helpers.PrintToAll(Translator.Instance["cr.score", _cr.ScoreLine(), _cr.CurrentHalf]);
        }

        switch (matchEvent)
        {
            case CrMatchEvent.HalftimeReached:
                Helpers.PrintToAll(Translator.Instance["cr.halftime"]);
                _plugin.AddTimer(0.3f, EnforceCrSides);
                break;
            case CrMatchEvent.MatchWonByA:
                FinishCrMatch("A");
                return;
            case CrMatchEvent.MatchWonByB:
                FinishCrMatch("B");
                return;
            case CrMatchEvent.MatchDraw:
                FinishCrMatch("draw");
                return;
        }

        // Roster wiped out? Cancel with no ELO.
        if (Configs.GetConfigData().Competitive.CancelWhenRosterEmpty)
        {
            var connected = Helpers.GetTeamHumanPlayers()
                .Select(Helpers.GetSteamId).ToHashSet();
            if (!_cr.TeamA.Any(connected.Contains) || !_cr.TeamB.Any(connected.Contains))
            {
                CancelCrMatch("cancelled");
                return;
            }
        }

        PushNextCrRoundPlan();
    }

    /// <summary>
    /// Moves CR roster players onto their current correct side (used at halftime
    /// and every round poststart to defeat any other balancing).
    /// </summary>
    private void EnforceCrSides()
    {
        if (!_cr.IsLive)
        {
            return;
        }

        foreach (var player in Utilities.GetPlayers().Where(Helpers.IsHumanPlayer))
        {
            var steamId = Helpers.GetSteamId(player);
            var roster = _cr.RosterOf(steamId);
            if (roster is null)
            {
                continue;
            }

            var targetTeamNum = roster == "A" ? _cr.TeamASideTeamNum : _cr.TeamBSideTeamNum;
            var target = (CsTeam) targetTeamNum;
            if (player.Team != target)
            {
                // R12: ChangeTeam (not SwitchTeam) so the PAWN follows too —
                // fixes wrong skins/models after a side correction.
                player.ChangeTeam(target);
            }
        }
    }

    private void EnforceCrSpectatorRule(CCSPlayerController? player, int joinedTeam)
    {
        if (!_cr.IsLive || !Helpers.IsHumanPlayer(player))
        {
            return;
        }

        if (joinedTeam is not (2 or 3))
        {
            return;
        }

        var steamId = Helpers.GetSteamId(player);
        if (_cr.RosterOf(steamId) is not null)
        {
            return;
        }

        player!.ChangeTeam(CsTeam.Spectator);
        Helpers.WriteNewlineDelimited(Translator.Instance["cr.spectator_only"], player.PrintToChat);
    }

    private void FinishCrMatch(string result)
    {
        var seasonId = SeasonManager.Instance.ActiveSeasonId;
        var map = Server.MapName;
        var startedAt = _crMatchStartedUtc;
        var teamAKey = _cr.TeamAKey;
        var teamBKey = _cr.TeamBKey;
        var teamAName = _cr.TeamAName;
        var teamBName = _cr.TeamBName;
        var teamSize = _cr.TeamA.Count;
        var scoreA = _cr.ScoreA;
        var scoreB = _cr.ScoreB;

        Helpers.PrintToAll(Translator.Instance[result switch
        {
            "A" => "cr.match_won",
            "B" => "cr.match_won",
            _ => "cr.match_draw",
        }, result == "B" ? teamBName : teamAName, $"{scoreA}-{scoreB}"]);

        _cr.Cancel();
        _plugin.SuspendTeamManagement = false;
        ApplyModeCvars();

        Task.Run(() =>
        {
            try
            {
                var teamA = Queries.GetOrCreateCrTeam(seasonId, teamAKey, teamAName, teamSize);
                var teamB = Queries.GetOrCreateCrTeam(seasonId, teamBKey, teamBName, teamSize);

                var scoreForA = result switch {"A" => 1.0, "B" => 0.0, _ => 0.5};
                var (deltaA, deltaB) = TeamEloEngine.ComputeMatchDeltas(teamA.Elo, teamB.Elo, scoreForA);

                var (updatedA, updatedB) = Queries.PersistCrMatch(
                    seasonId, map, startedAt,
                    teamAKey, teamAName, teamBKey, teamBName, teamSize,
                    scoreA, scoreB, result, deltaA, deltaB);

                Server.NextFrame(() =>
                {
                    Helpers.PrintToAll(Translator.Instance["cr.team_elo",
                        teamAName, FormatDelta(deltaA), updatedA.Elo]);
                    Helpers.PrintToAll(Translator.Instance["cr.team_elo",
                        teamBName, FormatDelta(deltaB), updatedB.Elo]);
                });
            }
            catch (Exception e)
            {
                Log.Error($"Failed to persist CR match: {e}");
            }
        });
    }

    private void CancelCrMatch(string reason)
    {
        var seasonId = SeasonManager.Instance.ActiveSeasonId;
        var map = Server.MapName;
        var startedAt = _crMatchStartedUtc;
        var teamAKey = _cr.TeamAKey;
        var teamBKey = _cr.TeamBKey;
        var teamAName = _cr.TeamAName;
        var teamBName = _cr.TeamBName;
        var teamSize = _cr.TeamA.Count;
        var scoreA = _cr.ScoreA;
        var scoreB = _cr.ScoreB;

        _cr.Cancel();
        _crStopInitiator = 0;
        _plugin.SuspendTeamManagement = false;
        ApplyModeCvars();
        Helpers.PrintToAll(Translator.Instance["cr.match_cancelled"]);

        Task.Run(() =>
        {
            try
            {
                Queries.PersistCrMatch(seasonId, map, startedAt,
                    teamAKey, teamAName, teamBKey, teamBName, teamSize,
                    scoreA, scoreB, reason, 0, 0);
            }
            catch (Exception e)
            {
                Log.Error($"Failed to persist cancelled CR match: {e}");
            }
        });
    }

    private static string FormatDelta(int delta)
    {
        return delta >= 0 ? $"+{delta}" : delta.ToString();
    }

    public void OnCrTopCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.IsHumanPlayer(player) || !_dbReady)
        {
            return;
        }

        var steamId = Helpers.GetSteamId(player);
        var seasonId = SeasonManager.Instance.ActiveSeasonId;
        var seasonName = SeasonManager.Instance.ActiveSeasonName;

        Task.Run(() =>
        {
            var top = Queries.GetTopCrTeams(seasonId, 5);
            Server.NextFrame(() =>
            {
                var current = Utilities.GetPlayers()
                    .FirstOrDefault(p => Helpers.IsHumanPlayer(p) && Helpers.GetSteamId(p) == steamId);
                if (current is null)
                {
                    return;
                }

                Helpers.WriteNewlineDelimited(
                    Translator.Instance["cr.top_header", seasonName], current.PrintToChat);
                var rank = 1;
                foreach (var team in top)
                {
                    Helpers.WriteNewlineDelimited(
                        Translator.Instance["cr.top_entry", rank, team.PlayerNames, team.Elo,
                            team.MatchesWon, team.MatchesPlayed],
                        current.PrintToChat);
                    rank++;
                }

                if (top.Count == 0)
                {
                    Helpers.WriteNewlineDelimited(Translator.Instance["cr.top_empty"], current.PrintToChat);
                }
            });
        });
    }

    #endregion

    #region Map commands

    private void RegisterMapCommands()
    {
        foreach (var (alias, target) in Configs.GetConfigData().MapAliases)
        {
            var mapTarget = target;
            _plugin.AddCommand($"css_{alias}", $"Change map to {target}",
                (player, commandInfo) => HandleMapChange(player, mapTarget));
        }
    }

    private void HandleMapChange(CCSPlayerController? player, string target)
    {
        if (player is not null && !Helpers.IsHumanPlayer(player))
        {
            return;
        }

        var isAdmin = player is null || AdminManager.PlayerHasPermissions(player, "@css/root");
        if (!isAdmin &&
            Configs.GetConfigData().BlockMapChangeDuringMatch &&
            (_ranked.IsActive || _cr.IsLive || _crSetupActive))
        {
            Helpers.WriteNewlineDelimited(
                Translator.Instance["maps.blocked_during_match"], player!.PrintToChat);
            return;
        }

        var mapLabel = target.StartsWith("ws:") ? target[3..] : target;
        Helpers.PrintToAll(Translator.Instance["maps.changing",
            mapLabel, player?.PlayerName ?? "console"]);

        _plugin.AddTimer(1.5f, () =>
        {
            Server.ExecuteCommand(target.StartsWith("ws:")
                ? $"host_workshop_map {target[3..]}"
                : $"changelevel {target}");
        });
    }

    #endregion
}
