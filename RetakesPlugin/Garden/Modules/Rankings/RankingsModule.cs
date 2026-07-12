using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRankingsCore;
using GardenRankingsCore.Config;
using GardenRankingsCore.Db;
using GardenRankingsCore.Managers;
using GardenRankingsCore.Models;
using GardenRankingsCore.Rating;
using GardenRankingsCore.Utils;
using GardenRetakes.Core.GameModes;
using RetakesAllocatorShared;
using RetakesPlugin.Garden;
using SQLitePCL;
using static GardenRankingsCore.PluginInfo;
using GardenPlugin = RetakesPlugin.RetakesPlugin;

namespace GardenRankings;

/// <summary>
/// Garden port of the GardenRankings plugin as a module of the merged plugin
/// (ROADMAP R0). Donor logic kept intact; only the BasePlugin surface changed
/// (attributes -> explicit registrations, _plugin.* members, module lifecycle).
/// Round types still come from the allocator via IRetakesAllocatorApi — the
/// capability is now registered by AllocatorModule in the same assembly.
/// Config file: config/rankings.json (renamed — the allocator owns config.json).
/// </summary>
public partial class RankingsModule : IGardenModule
{
    private readonly GardenPlugin _plugin;
    private readonly GardenHost _host;

    private readonly RankedStateManager _ranked = new();
    private readonly RoundDataCollector _collector = new();
    private readonly AfkTracker _afk = new();

    // Hot caches for the game thread (ELO + ranked wins per connected player).
    private readonly Dictionary<ulong, int> _eloCache = new();
    private readonly Dictionary<ulong, int> _rankedWinsCache = new();

    private static readonly PluginCapability<IRetakesAllocatorApi> AllocatorApiCapability =
        new(RetakesAllocatorApiCapability.Name);

    private DateTime _lastAvailabilityAnnounceUtc = DateTime.MinValue;
    private bool _warmupHoldActive;
    private bool _seasonRotationInProgress;
    private bool _dbReady;

    private class DamageEntry
    {
        public int Damage { get; set; }
        public int Hits { get; set; }
    }

    private readonly HashSet<ulong> _optOutDamageReport = new();
    private readonly Dictionary<ulong, Dictionary<ulong, DamageEntry>> _damageGivenThisRound = new();

    // Test mode (css_rr_force): ranked stays active regardless of player count.
    private bool _testBypassMinPlayers;

    public string Name => "Rankings";
    public bool Enabled => _host.Settings.Rankings.Enabled;

    public RankingsModule(GardenPlugin plugin, GardenHost host)
    {
        _plugin = plugin;
        _host = host;
    }

    #region Setup

    public void Load(bool hotReload)
    {
        if (!Enabled)
        {
            return;
        }

        Configs.Shared.Module = _plugin.ModuleDirectory;
        Configs.Load(_plugin.ModuleDirectory, true);
        Translator.Initialize(_plugin.Localizer);
        Batteries.Init();

        Task.Run(() =>
        {
            try
            {
                Queries.Initialize();
                SeasonManager.Instance.Initialize();
                _dbReady = true;
                Log.Info($"Database ready. Active season: {SeasonManager.Instance.ActiveSeasonName}");
            }
            catch (Exception e)
            {
                Log.Error($"Failed to initialize database: {e}");
            }
        });

        // The competitive-rank fields are reset by the game, so they must be
        // re-applied continuously for the scoreboard to keep showing the rating
        // (same approach as K4-System-MMRanks).
        _plugin.RegisterListener<Listeners.OnTick>(OnTickScoreboard);

        // Persistent repeating timers (survive map changes).
        _plugin.AddTimer(1.0f, OnSecondTimer, TimerFlags.REPEAT);
        _plugin.AddTimer(0.5f, OnAfkSampleTimer, TimerFlags.REPEAT);

        // Round lifecycle + stat collection events (donor used [GameEventHandler]).
        _plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        _plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        _plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        _plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        _plugin.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        _plugin.RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        _plugin.RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        _plugin.RegisterEventHandler<EventBombDefused>(OnBombDefused);
        _plugin.RegisterEventHandler<EventBombExploded>(OnBombExploded);
        _plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        _plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        _plugin.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        // Clutch/CR team enforcement: poststart PRE runs after every
        // round_prestart handler (the retakes balancer) and before the Post
        // handler that allocates spawns (gotcha #2 in ROADMAP.md).
        _plugin.RegisterEventHandler<EventRoundPoststart>(OnRoundPoststartPre, HookMode.Pre);

        // Ranked commands.
        _plugin.AddCommand("css_rr", "Start or stop a Ranked Retakes session.", OnRankedCommand);
        _plugin.AddCommand("css_ranked", "Start or stop a Ranked Retakes session.", OnRankedCommand);
        _plugin.AddCommand("css_ry", "Accept the ongoing vote.", OnVoteYesCommand);
        _plugin.AddCommand("css_rn", "Decline the ongoing vote.", OnVoteNoCommand);
        _plugin.AddCommand("css_rankedstatus", "Shows the Ranked Retakes status.", OnRankedStatusCommand);

        // Stats commands.
        _plugin.AddCommand("css_elo", "Shows your ELO and ladder placement.", OnEloCommand);
        _plugin.AddCommand("css_stats", "Shows your season stats. Usage: !stats [ranked]", OnStatsCommand);
        _plugin.AddCommand("css_top", "Shows the season's top rated players.", OnTopCommand);
        _plugin.AddCommand("css_dmg", "Toggles the damage report preview.", OnDmgCommand);
        _plugin.AddCommand("css_damage", "Toggles the damage report preview.", OnDmgCommand);

        // Competitive Retakes.
        _plugin.AddCommand("css_cr", "Start (or stop) a Competitive Retakes match.", OnCrCommand);
        _plugin.AddCommand("css_crtop", "Shows the season's top Competitive Retakes teams.", OnCrTopCommand);

        // Test commands (admin).
        _plugin.AddCommand("css_rr_force", "TEST: force-activate Ranked Retakes.", OnRankedForceCommand);
        _plugin.AddCommand("css_rr_stop", "TEST: force-stop Ranked Retakes.", OnRankedForceStopCommand);
        _plugin.AddCommand("css_rr_state", "TEST: print the ranked session state.", OnRankedStateCommand);
        _plugin.AddCommand("css_rr_setelo", "TEST: set your own ELO. Usage: css_rr_setelo <elo>", OnSetEloCommand);
        _plugin.AddCommand("css_rr_test_round",
            "TEST: simulate a ranked round. Usage: css_rr_test_round [win|loss] [kills]", OnTestRoundCommand);

        // Admin commands.
        _plugin.AddCommand("css_season_new", "Starts a new season. Usage: css_season_new [name]", OnNewSeasonCommand);
        _plugin.AddCommand("css_seasons", "Lists all seasons.", OnSeasonsCommand);
        _plugin.AddCommand("css_rankings_reload_config", "Reloads the rankings config.", OnReloadConfigCommand);
        _plugin.AddCommand("css_autoscramble", "Toggles auto team scramble every round.", OnAutoScrambleCommand);

        RegisterMapCommands();

        // R10: single transition point for cvar profiles — whenever the server
        // returns to Retakes mode (from Duels/Executes/FastStrat), re-apply the
        // correct Classic/Ranked/Competitive profile. The other modes apply
        // their own Start/StopCommands on their transitions.
        _host.Modes.ModeChanged += (_, to) =>
        {
            if (to == GameModeKind.Retakes)
            {
                _plugin.AddTimer(0.5f, ApplyModeCvars);
            }
        };

        Log.Debug($"Rankings module loaded. Hot reload: {hotReload}");
    }

    public void Unload()
    {
        if (!Enabled)
        {
            return;
        }

        Queries.Disconnect();
        Log.Debug("Rankings module unloaded");
    }

    public void OnMapStart(string mapName)
    {
        if (!Enabled)
        {
            return;
        }

        _collector.Abort();
        OnMapStartModes();

        if (_ranked.IsActive)
        {
            // Ranked mode survives map changes: hold warmup until enough players are back.
            _warmupHoldActive = true;
            var warmupSeconds = Configs.GetConfigData().Ranked.RankedWarmupMaxSeconds;
            Server.NextFrame(() =>
            {
                Server.ExecuteCommand($"mp_warmuptime {warmupSeconds}");
                Server.ExecuteCommand("mp_warmup_start");
                Helpers.PrintToAll(Translator.Instance["ranked.warmup_hold",
                    Configs.GetConfigData().Ranked.MinPlayers]);
            });
        }
    }

    private IRetakesAllocatorApi? GetAllocatorApi()
    {
        try
        {
            return AllocatorApiCapability.Get();
        }
        catch (Exception e)
        {
            Log.Debug($"Allocator API unavailable: {e.Message}");
            return null;
        }
    }

    /// <summary>Replacement for the [RequiresPermissions("@css/root")] attribute (console always passes).</summary>
    private static bool HasRootPermission(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null || AdminManager.PlayerHasPermissions(player, "@css/root"))
        {
            return true;
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}You don't have permission to use this command.");
        return false;
    }

    #endregion

    #region Round lifecycle

    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        PrintDamageReports();
        _damageGivenThisRound.Clear();
        return HookResult.Continue;
    }

    private void PrintDamageReports()
    {
        foreach (var player in Utilities.GetPlayers().Where(Helpers.IsHumanPlayer))
        {
            var steamId = Helpers.GetSteamId(player);
            if (steamId == 0 || _optOutDamageReport.Contains(steamId))
            {
                continue;
            }

            bool printedHeader = false;
            if (_damageGivenThisRound.TryGetValue(steamId, out var given))
            {
                foreach (var (victimId, entry) in given)
                {
                    var victim = Utilities.GetPlayers().FirstOrDefault(p => Helpers.GetSteamId(p) == victimId);
                    if (victim != null)
                    {
                        if (!printedHeader)
                        {
                            player.PrintToChat($" \x08[\x0C DAMAGE \x08]-------------------------");
                            printedHeader = true;
                        }
                        player.PrintToChat($" \x08Damage Given to \x04{victim.PlayerName}\x08 - \x06{entry.Damage}\x08 in \x06{entry.Hits}\x08 hits");
                    }
                }
            }

            foreach (var (attackerId, attackerDamage) in _damageGivenThisRound)
            {
                if (attackerDamage.TryGetValue(steamId, out var receivedEntry))
                {
                    var attacker = Utilities.GetPlayers().FirstOrDefault(p => Helpers.GetSteamId(p) == attackerId);
                    if (attacker != null)
                    {
                        if (!printedHeader)
                        {
                            player.PrintToChat($" \x08[\x0C DAMAGE \x08]-------------------------");
                            printedHeader = true;
                        }
                        player.PrintToChat($" \x08Damage Taken from \x02{attacker.PlayerName}\x08 - \x06{receivedEntry.Damage}\x08 in \x06{receivedEntry.Hits}\x08 hits");
                    }
                }
            }
        }
    }

    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (@event == null! || Helpers.IsWarmup() || !_dbReady ||
            // Garden (R4): no retakes stats while Duels/Executes/FastStrat run.
            _host.Modes.CurrentMode != GameModeKind.Retakes)
        {
            return HookResult.Continue;
        }

        var roundType = RetakesRoundType.Unknown;
        var api = GetAllocatorApi();
        if (api?.CurrentRoundTypeOrdinal is { } ordinal && Enum.IsDefined(typeof(RetakesRoundType), ordinal))
        {
            roundType = (RetakesRoundType) ordinal;
        }

        var startingElo = Configs.GetConfigData().Elo.StartingElo;
        var players = new List<(ulong, string, int, int)>();
        foreach (var player in Helpers.GetTeamHumanPlayers())
        {
            var steamId = Helpers.GetSteamId(player);
            if (steamId == 0)
            {
                continue;
            }

            players.Add((
                steamId,
                player.PlayerName,
                player.TeamNum,
                _eloCache.TryGetValue(steamId, out var elo) ? elo : startingElo
            ));
        }

        var isRanked = _ranked.IsActive &&
                       (_testBypassMinPlayers || players.Count >= Configs.GetConfigData().Ranked.MinPlayers);

        _collector.BeginRound(Server.MapName, roundType, isRanked, players);
        _afk.BeginRound(players.Select(p => p.Item1));

        AnnounceClutchRoundIfPending();

        return HookResult.Continue;
    }

    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (@event == null!)
        {
            return HookResult.Continue;
        }

        // Mode logic (CR score/halves, clutch scheduling, classic scramble) runs
        // even when the stat collector skipped the round — but only in Retakes mode.
        if (!Helpers.IsWarmup() && _host.Modes.CurrentMode == GameModeKind.Retakes)
        {
            HandleModesRoundEnd(@event.Winner);
        }

        if (!_collector.IsCollecting)
        {
            return HookResult.Continue;
        }

        var (ctx, players) = _collector.EndRound(@event.Winner);
        if (players.Count == 0)
        {
            return HookResult.Continue;
        }

        // AFK flags first: fully passive players with zero damage output.
        foreach (var p in players)
        {
            if (_afk.WasAfkThisRound(p.SteamId) && p.Damage == 0 && p.Kills == 0)
            {
                p.WasAfk = true;
            }
        }

        EloEngine.FlagEarlyDeaths(players);
        RatingEngine.ComputeRatings(players, ctx);
        EloEngine.ApplyEloDeltas(players, ctx);

        HandleAfkSpectateMoves(players);

        if (ctx.IsRanked && Configs.GetConfigData().Announcements.EloChangeToPlayer)
        {
            AnnounceEloChanges(players);
        }

        PersistRoundAsync(ctx, players);

        return HookResult.Continue;
    }

    private void HandleAfkSpectateMoves(List<PlayerRoundStats> players)
    {
        var afkIds = players.Where(p => p.WasAfk).Select(p => p.SteamId).ToList();
        var activeIds = players.Where(p => !p.WasAfk).Select(p => p.SteamId).ToList();
        var toSpectate = _afk.OnRoundEnd(afkIds, activeIds);

        foreach (var steamId in toSpectate)
        {
            var player = Utilities.GetPlayers()
                .FirstOrDefault(p => Helpers.IsHumanPlayer(p) && Helpers.GetSteamId(p) == steamId);
            if (player is null)
            {
                continue;
            }

            player.ChangeTeam(CsTeam.Spectator);
            Helpers.PrintToAll(Translator.Instance["afk.moved_to_spectators", player.PlayerName]);
        }
    }

    private void AnnounceEloChanges(List<PlayerRoundStats> players)
    {
        foreach (var p in players)
        {
            var player = Utilities.GetPlayers()
                .FirstOrDefault(pl => Helpers.IsHumanPlayer(pl) && Helpers.GetSteamId(pl) == p.SteamId);
            if (player is null)
            {
                continue;
            }

            if (p.WasAfk)
            {
                Helpers.WriteNewlineDelimited(Translator.Instance["elo.afk_no_change"], player.PrintToChat);
                continue;
            }

            var key = p.EloDelta >= 0 ? "elo.round_gain" : "elo.round_loss";
            Helpers.WriteNewlineDelimited(
                Translator.Instance[key, Math.Abs(p.EloDelta), p.EloAfter, p.Rating.ToString("0.00")],
                player.PrintToChat);
        }
    }

    private void PersistRoundAsync(RoundContext ctx, List<PlayerRoundStats> players)
    {
        var seasonId = SeasonManager.Instance.ActiveSeasonId;
        var announcements = Configs.GetConfigData().Announcements;

        Task.Run(() =>
        {
            try
            {
                var updatedStats = Queries.PersistRound(seasonId, ctx, players);

                var recordMessages = new List<string>();
                if (ctx.IsRanked)
                {
                    foreach (var p in players.Where(p => !p.WasAfk))
                    {
                        var records = SeasonManager.Instance.CheckRecords(p.SteamId, p.EloAfter);
                        if (records.ServerRecordBroken && announcements.ServerRecordBroken)
                        {
                            recordMessages.Add(Translator.Instance[
                                "records.server_record_broken",
                                p.PlayerName, p.EloAfter,
                                records.PreviousServerRecordHolder ?? "?",
                                records.PreviousServerRecord ?? 0]);
                            _ = DiscordWebhook.SendAsync(announcements.DiscordWebhookUrl, 
                                $"🔥 **NEW SERVER RECORD!** {p.PlayerName} just reached **{p.EloAfter}** ELO!");
                        }

                        if (records.PersonalBestBroken && announcements.PersonalBestBroken)
                        {
                            recordMessages.Add(Translator.Instance[
                                "records.personal_best_broken",
                                p.PlayerName, p.EloAfter, records.PreviousPersonalBest ?? 0]);
                            _ = DiscordWebhook.SendAsync(announcements.DiscordWebhookUrl, 
                                $"📈 **Personal Best:** {p.PlayerName} just hit a new peak of **{p.EloAfter}** ELO!");
                        }
                    }
                }

                foreach (var p in players.Where(p => p.ClutchWon && p.ClutchVersus >= 3))
                {
                    _ = DiscordWebhook.SendAsync(announcements.DiscordWebhookUrl, 
                        $"🔥 **CLUTCH!** {p.PlayerName} just won a **1v{p.ClutchVersus}** clutch on {ctx.Map}!");
                }

                Server.NextFrame(() =>
                {
                    foreach (var (steamId, stats) in updatedStats)
                    {
                        _eloCache[steamId] = stats.Elo;
                        _rankedWinsCache[steamId] = stats.RankedRoundsWon;
                    }

                    RefreshAllScoreboards();

                    foreach (var message in recordMessages)
                    {
                        Helpers.PrintToAll(message);
                    }
                });
            }
            catch (Exception e)
            {
                Log.Error($"Failed to persist round: {e}");
            }
        });
    }

    private void RefreshAllScoreboards()
    {
        var startingElo = Configs.GetConfigData().Elo.StartingElo;
        foreach (var player in Utilities.GetPlayers().Where(Helpers.IsHumanPlayer))
        {
            var steamId = Helpers.GetSteamId(player);
            if (steamId == 0)
            {
                continue;
            }

            ScoreboardManager.SetPlayerScoreboardElo(
                player,
                _eloCache.TryGetValue(steamId, out var elo) ? elo : startingElo,
                _rankedWinsCache.TryGetValue(steamId, out var wins) ? wins : 0
            );
        }
    }

    #endregion

    #region Stat collection events

    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (@event == null! || !_collector.IsCollecting)
        {
            return HookResult.Continue;
        }

        _collector.OnPlayerDeath(
            Helpers.GetSteamId(@event.Userid),
            Helpers.GetSteamId(@event.Attacker),
            Helpers.GetSteamId(@event.Assister),
            @event.Headshot,
            @event.Assistedflash
        );

        return HookResult.Continue;
    }

    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (@event == null!)
        {
            return HookResult.Continue;
        }

        var attackerId = Helpers.GetSteamId(@event.Attacker);
        var victimId = Helpers.GetSteamId(@event.Userid);

        if (attackerId != 0 && victimId != 0 && attackerId != victimId)
        {
            if (!_damageGivenThisRound.TryGetValue(attackerId, out var attackerDamage))
            {
                attackerDamage = new Dictionary<ulong, DamageEntry>();
                _damageGivenThisRound[attackerId] = attackerDamage;
            }

            if (!attackerDamage.TryGetValue(victimId, out var entry))
            {
                entry = new DamageEntry();
                attackerDamage[victimId] = entry;
            }

            entry.Damage += @event.DmgHealth;
            entry.Hits++;
        }

        if (!_collector.IsCollecting)
        {
            return HookResult.Continue;
        }

        _collector.OnPlayerHurt(
            victimId,
            attackerId,
            @event.DmgHealth,
            @event.Weapon
        );

        return HookResult.Continue;
    }

    public HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        if (@event == null! || !_collector.IsCollecting)
        {
            return HookResult.Continue;
        }

        _collector.OnPlayerBlind(
            Helpers.GetSteamId(@event.Userid),
            Helpers.GetSteamId(@event.Attacker),
            @event.BlindDuration
        );

        return HookResult.Continue;
    }

    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (@event == null! || !_collector.IsCollecting)
        {
            return HookResult.Continue;
        }

        _collector.OnBombPlanted(Helpers.GetSteamId(@event.Userid), @event.Site.ToString());
        return HookResult.Continue;
    }

    public HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        if (@event == null! || !_collector.IsCollecting)
        {
            return HookResult.Continue;
        }

        _collector.OnBombDefused(Helpers.GetSteamId(@event.Userid));
        return HookResult.Continue;
    }

    public HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        if (@event == null!)
        {
            return HookResult.Continue;
        }

        _collector.OnBombExploded();
        return HookResult.Continue;
    }

    #endregion

    #region Player lifecycle

    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event?.Userid;
        if (!Helpers.IsHumanPlayer(player) || !_dbReady)
        {
            return HookResult.Continue;
        }

        var steamId = Helpers.GetSteamId(player);
        var name = player!.PlayerName;
        if (steamId == 0)
        {
            return HookResult.Continue;
        }

        var seasonId = SeasonManager.Instance.ActiveSeasonId;
        var startingElo = Configs.GetConfigData().Elo.StartingElo;
        var announceJoin = Configs.GetConfigData().Announcements.JoinPlacement;

        Task.Run(() =>
        {
            try
            {
                // W2: ban enforcement on connect.
                var ban = Queries.GetActiveBan(steamId);
                if (ban is not null)
                {
                    Server.NextFrame(() =>
                    {
                        var banned = Utilities.GetPlayers()
                            .FirstOrDefault(p => Helpers.IsHumanPlayer(p) && Helpers.GetSteamId(p) == steamId);
                        if (banned?.UserId is not null)
                        {
                            Server.ExecuteCommand($"kickid {banned.UserId} Banned: {ban.Reason}");
                        }
                    });
                    return;
                }

                // W2: website-chosen display name wins over the Steam name.
                var overrideName = Queries.GetNameOverride(steamId);
                if (!string.IsNullOrWhiteSpace(overrideName))
                {
                    name = overrideName;
                }

                Queries.UpsertPlayerProfile(steamId, name);
                SeasonManager.Instance.PreloadPersonalBest(steamId);
                var stats = Queries.GetSeasonStats(seasonId, steamId);
                var placement = Queries.GetPlacement(seasonId, steamId);

                Server.NextFrame(() =>
                {
                    _eloCache[steamId] = stats?.Elo ?? startingElo;
                    _rankedWinsCache[steamId] = stats?.RankedRoundsWon ?? 0;

                    var current = Utilities.GetPlayers()
                        .FirstOrDefault(p => Helpers.IsHumanPlayer(p) && Helpers.GetSteamId(p) == steamId);
                    if (current is not null)
                    {
                        // W2: apply the override in game (scoreboard, kill feed, stats).
                        if (!string.IsNullOrWhiteSpace(overrideName) && current.PlayerName != overrideName)
                        {
                            current.PlayerName = overrideName;
                            Utilities.SetStateChanged(current, "CBasePlayerController", "m_iszPlayerName");
                        }

                        ScoreboardManager.SetPlayerScoreboardElo(
                            current, _eloCache[steamId], _rankedWinsCache[steamId]);
                    }

                    if (announceJoin)
                    {
                        Helpers.PrintToAll(placement is null
                            ? Translator.Instance["join.unranked", name]
                            : Translator.Instance["join.placement", name, placement.Rank, placement.Elo]);
                    }
                });
            }
            catch (Exception e)
            {
                Log.Error($"Failed to load player {steamId}: {e}");
            }
        });

        return HookResult.Continue;
    }

    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event?.Userid;
        if (player is null)
        {
            return HookResult.Continue;
        }

        var steamId = Helpers.GetSteamId(player);
        if (steamId != 0)
        {
            _eloCache.Remove(steamId);
            _rankedWinsCache.Remove(steamId);
            _afk.ForgetPlayer(steamId);
        }

        Server.NextFrame(CheckRankedPlayerCount);

        return HookResult.Continue;
    }

    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        if (@event == null!)
        {
            return HookResult.Continue;
        }

        var joiner = @event.Userid;
        var joinedTeam = @event.Team;

        Server.NextFrame(() =>
        {
            EnforceCrSpectatorRule(joiner, joinedTeam);
            CheckRankedPlayerCount();
            CheckWarmupHold();
        });

        return HookResult.Continue;
    }

    private void CheckRankedPlayerCount()
    {
        if (_testBypassMinPlayers)
        {
            return;
        }

        var count = Helpers.CountTeamHumanPlayers();
        if (_ranked.OnEligiblePlayerCountChanged(count))
        {
            Helpers.PrintToAll(Translator.Instance["ranked.stopped_not_enough_players",
                Configs.GetConfigData().Ranked.MinPlayers]);
        }
    }

    private void CheckWarmupHold()
    {
        if (!_warmupHoldActive || !Helpers.IsWarmup())
        {
            return;
        }

        if (Helpers.CountTeamHumanPlayers() >= Configs.GetConfigData().Ranked.MinPlayers)
        {
            _warmupHoldActive = false;
            Server.ExecuteCommand("mp_warmup_end");
            Helpers.PrintToAll(Translator.Instance["ranked.warmup_hold_over"]);
        }
    }

    #endregion

    #region Timers

    private void OnSecondTimer()
    {
        if (!Configs.IsLoaded())
        {
            return;
        }

        HandleVoteTick();
        HandleCrTick();
        CheckAutoRankedToggle();
        HandleAvailabilityAnnounce();
        CheckWarmupHold();
        HandleSeasonAutoRotate();
    }

    /// <summary>
    /// Automatic Ranked Retakes: activates the moment enough players are on teams.
    /// Deactivation below the minimum is handled by CheckRankedPlayerCount.
    /// </summary>
    private void CheckAutoRankedToggle()
    {
        if (!Configs.GetConfigData().Ranked.AutoActivate || !_dbReady ||
            _cr.IsLive || _crSetupActive ||
            _ranked.State != RankedState.Inactive ||
            // Garden (R4): never auto-activate ranked while Duels/Executes/FastStrat run.
            _host.Modes.CurrentMode != GameModeKind.Retakes)
        {
            return;
        }

        if (Helpers.CountTeamHumanPlayers() >= Configs.GetConfigData().Ranked.MinPlayers)
        {
            _ranked.ForceActivate();
            AnnounceRankedActivated();
        }
    }

    private void HandleVoteTick()
    {
        var outcome = _ranked.Tick(DateTime.UtcNow);
        switch (outcome)
        {
            case TickOutcome.StartVotePassed:
                AnnounceRankedActivated();
                break;
            case TickOutcome.StartVoteFailed:
                Helpers.PrintToAll(Translator.Instance["ranked.vote_failed"]);
                break;
            case TickOutcome.StopVotePassed:
                AnnounceRankedDeactivated();
                break;
            case TickOutcome.StopVoteFailed:
                Helpers.PrintToAll(Translator.Instance["ranked.stop_vote_failed"]);
                break;
            case TickOutcome.StopConfirmExpired:
                Helpers.PrintToAll(Translator.Instance["ranked.stop_confirm_expired"]);
                break;
        }
    }

    private void HandleAvailabilityAnnounce()
    {
        var cfg = Configs.GetConfigData().Ranked;
        if (cfg.AutoActivate || !cfg.AnnounceAvailability ||
            _ranked.State != RankedState.Inactive || Helpers.IsWarmup())
        {
            return;
        }

        if (Helpers.CountTeamHumanPlayers() < cfg.MinPlayers)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastAvailabilityAnnounceUtc).TotalSeconds < cfg.AvailabilityAnnounceIntervalSeconds)
        {
            return;
        }

        _lastAvailabilityAnnounceUtc = now;
        Helpers.PrintToAll(Translator.Instance["ranked.available"]);
    }

    private void HandleSeasonAutoRotate()
    {
        if (!_dbReady || _seasonRotationInProgress ||
            Configs.GetConfigData().Season.AutoRotateDays <= 0)
        {
            return;
        }

        if (DateTime.UtcNow < SeasonManager.Instance.ActiveSeasonStartedAtUtc
                .AddDays(Configs.GetConfigData().Season.AutoRotateDays))
        {
            return;
        }

        _seasonRotationInProgress = true;
        Task.Run(() =>
        {
            try
            {
                var season = SeasonManager.Instance.CheckAutoRotate(DateTime.UtcNow);
                if (season is not null)
                {
                    Server.NextFrame(() => OnSeasonChanged(season.Name));
                }
            }
            catch (Exception e)
            {
                Log.Error($"Season auto-rotation failed: {e}");
            }
            finally
            {
                _seasonRotationInProgress = false;
            }
        });
    }

    private void OnSeasonChanged(string seasonName)
    {
        var startingElo = Configs.GetConfigData().Elo.StartingElo;
        foreach (var key in _eloCache.Keys.ToList())
        {
            _eloCache[key] = startingElo;
            _rankedWinsCache[key] = 0;
        }

        RefreshAllScoreboards();
        Helpers.PrintToAll(Translator.Instance["season.started", seasonName]);
    }

    private void OnTickScoreboard()
    {
        if (!Configs.IsLoaded() || !Configs.GetConfigData().EnableScoreboardRanks)
        {
            return;
        }

        var startingElo = Configs.GetConfigData().Elo.StartingElo;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!Helpers.IsHumanPlayer(player))
            {
                continue;
            }

            var steamId = Helpers.GetSteamId(player);
            if (steamId == 0)
            {
                continue;
            }

            ScoreboardManager.SetPlayerScoreboardElo(
                player,
                _eloCache.TryGetValue(steamId, out var elo) ? elo : startingElo,
                _rankedWinsCache.TryGetValue(steamId, out var wins) ? wins : 0
            );
        }
    }

    private void OnAfkSampleTimer()
    {
        if (!_collector.IsCollecting)
        {
            return;
        }

        foreach (var player in Helpers.GetTeamHumanPlayers())
        {
            if (!player.PawnIsAlive)
            {
                continue;
            }

            var steamId = Helpers.GetSteamId(player);
            var origin = player.PlayerPawn.Value?.AbsOrigin;
            if (steamId == 0 || origin is null)
            {
                continue;
            }

            _afk.Sample(steamId, origin.X, origin.Y, origin.Z, (ulong) player.Buttons);
        }
    }

    #endregion

    #region Ranked commands

    public void OnRankedCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        HandleRankedToggle(player, commandInfo);
    }

    private void HandleRankedToggle(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.IsHumanPlayer(player) || !_dbReady)
        {
            return;
        }

        if (_cr.IsLive)
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["cr.blocks_ranked"], player!.PrintToChat);
            return;
        }

        if (Configs.GetConfigData().Ranked.AutoActivate)
        {
            Helpers.WriteNewlineDelimited(
                Translator.Instance["ranked.auto_mode", Configs.GetConfigData().Ranked.MinPlayers],
                player!.PrintToChat);
            return;
        }

        var steamId = Helpers.GetSteamId(player);
        var now = DateTime.UtcNow;
        var eligible = Helpers.GetTeamHumanPlayers()
            .Select(Helpers.GetSteamId)
            .Where(id => id != 0)
            .ToList();

        // Confirming a pending exit?
        if (_ranked.State == RankedState.StopConfirmPending && _ranked.VoteInitiator == steamId)
        {
            if (_ranked.ConfirmStop(steamId, now))
            {
                AnnounceRankedDeactivated();
            }

            return;
        }

        if (_ranked.IsActive)
        {
            var stopOutcome = _ranked.RequestStop(steamId, eligible, now);
            switch (stopOutcome)
            {
                case StopRequestOutcome.ConfirmationPending:
                    Helpers.WriteNewlineDelimited(
                        Translator.Instance["ranked.stop_confirm_prompt",
                            (int) Configs.GetConfigData().Ranked.StopConfirmSeconds],
                        player!.PrintToChat);
                    break;
                case StopRequestOutcome.StopVoteStarted:
                    Helpers.PrintToAll(Translator.Instance["ranked.stop_vote_started",
                        player!.PlayerName, (int) Configs.GetConfigData().Ranked.VoteDurationSeconds]);
                    break;
                case StopRequestOutcome.VoteAlreadyInProgress:
                    Helpers.WriteNewlineDelimited(
                        Translator.Instance["ranked.vote_in_progress"], player!.PrintToChat);
                    break;
            }

            return;
        }

        var outcome = _ranked.TryStartVote(steamId, eligible, now);
        switch (outcome)
        {
            case StartVoteOutcome.VoteStarted:
                Helpers.PrintToAll(Translator.Instance["ranked.vote_started",
                    player!.PlayerName, (int) Configs.GetConfigData().Ranked.VoteDurationSeconds]);
                break;
            case StartVoteOutcome.NotEnoughPlayers:
                Helpers.WriteNewlineDelimited(
                    Translator.Instance["ranked.not_enough_players", Configs.GetConfigData().Ranked.MinPlayers],
                    player!.PrintToChat);
                break;
            case StartVoteOutcome.VoteAlreadyInProgress:
                Helpers.WriteNewlineDelimited(
                    Translator.Instance["ranked.vote_in_progress"], player!.PrintToChat);
                break;
        }
    }

    public void OnVoteYesCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        HandleVote(player, accept: true);
    }

    public void OnVoteNoCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        HandleVote(player, accept: false);
    }

    private void HandleVote(CCSPlayerController? player, bool accept)
    {
        if (!Helpers.IsHumanPlayer(player))
        {
            return;
        }

        // R12: CR no longer votes — /ry /rn are ranked-only now.
        var steamId = Helpers.GetSteamId(player);
        var wasStopVote = _ranked.State == RankedState.StopVoteInProgress;
        var result = _ranked.CastVote(steamId, accept, DateTime.UtcNow);

        switch (result)
        {
            case VoteCastResult.Passed:
                if (wasStopVote)
                {
                    AnnounceRankedDeactivated();
                }
                else
                {
                    AnnounceRankedActivated();
                }

                break;
            case VoteCastResult.Failed:
                Helpers.PrintToAll(Translator.Instance[
                    wasStopVote ? "ranked.stop_vote_failed" : "ranked.vote_failed"]);
                break;
            case VoteCastResult.Registered:
                Helpers.PrintToAll(Translator.Instance["ranked.vote_progress",
                    _ranked.Accepted.Count, _ranked.EligibleVoters.Count]);
                break;
            case VoteCastResult.NoVoteInProgress:
                Helpers.WriteNewlineDelimited(
                    Translator.Instance["ranked.no_vote_in_progress"], player!.PrintToChat);
                break;
            case VoteCastResult.NotEligible:
                Helpers.WriteNewlineDelimited(
                    Translator.Instance["ranked.not_eligible"], player!.PrintToChat);
                break;
            case VoteCastResult.AlreadyVoted:
                Helpers.WriteNewlineDelimited(
                    Translator.Instance["ranked.already_voted"], player!.PrintToChat);
                break;
        }
    }

    private void AnnounceRankedActivated()
    {
        Helpers.PrintToAll(Translator.Instance["ranked.activated"]);
        ApplyModeCvars();
    }

    private void AnnounceRankedDeactivated()
    {
        Helpers.PrintToAll(Translator.Instance["ranked.deactivated"]);
        ApplyModeCvars();
    }

    public void OnRankedStatusCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var key = _ranked.IsActive ? "ranked.status_active" : "ranked.status_inactive";
        Helpers.WriteNewlineDelimited(Translator.Instance[key], player!.PrintToChat);
    }

    #endregion

    #region Stats commands

    public void OnEloCommand(CCSPlayerController? player, CommandInfo commandInfo)
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
            var placement = Queries.GetPlacement(seasonId, steamId);
            Server.NextFrame(() =>
            {
                var current = Utilities.GetPlayers()
                    .FirstOrDefault(p => Helpers.IsHumanPlayer(p) && Helpers.GetSteamId(p) == steamId);
                if (current is null)
                {
                    return;
                }

                Helpers.WriteNewlineDelimited(placement is null
                        ? Translator.Instance["elo.self_unranked", seasonName]
                        : Translator.Instance["elo.self", placement.Elo, placement.Rank, placement.TotalRanked,
                            seasonName],
                    current.PrintToChat);
            });
        });
    }

    public void OnStatsCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.IsHumanPlayer(player) || !_dbReady)
        {
            return;
        }

        var steamId = Helpers.GetSteamId(player);
        var rankedOnly = commandInfo.ArgCount > 1 &&
                         commandInfo.GetArg(1).Equals("ranked", StringComparison.OrdinalIgnoreCase);
        var seasonId = SeasonManager.Instance.ActiveSeasonId;
        var seasonName = SeasonManager.Instance.ActiveSeasonName;

        Task.Run(() =>
        {
            var s = Queries.GetPlayerSeasonSummary(seasonId, steamId, rankedOnly);
            Server.NextFrame(() =>
            {
                var current = Utilities.GetPlayers()
                    .FirstOrDefault(p => Helpers.IsHumanPlayer(p) && Helpers.GetSteamId(p) == steamId);
                if (current is null)
                {
                    return;
                }

                var kd = s.Deaths > 0 ? (double) s.Kills / s.Deaths : s.Kills;
                var hsPercent = s.Kills > 0 ? 100.0 * s.Headshots / s.Kills : 0;
                Helpers.WriteNewlineDelimited(
                    Translator.Instance["stats.header", seasonName, rankedOnly ? " (ranked)" : ""] + "\n" +
                    Translator.Instance["stats.line1", s.RoundsPlayed, s.Elo, s.PeakElo,
                        s.AverageRating.ToString("0.00")] + "\n" +
                    Translator.Instance["stats.line2", s.Kills, s.Deaths, s.Assists, kd.ToString("0.00"),
                        hsPercent.ToString("0")] + "\n" +
                    Translator.Instance["stats.line3", s.AverageDamagePerRound.ToString("0"),
                        s.KastPercent.ToString("0"), s.ClutchWins, s.OpeningKills, s.BombPlants, s.BombDefuses],
                    current.PrintToChat);
            });
        });
    }

    public void OnTopCommand(CCSPlayerController? player, CommandInfo commandInfo)
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
            var top = Queries.GetTopPlayers(seasonId, 10);
            Server.NextFrame(() =>
            {
                var current = Utilities.GetPlayers()
                    .FirstOrDefault(p => Helpers.IsHumanPlayer(p) && Helpers.GetSteamId(p) == steamId);
                if (current is null)
                {
                    return;
                }

                Helpers.WriteNewlineDelimited(Translator.Instance["top.header", seasonName], current.PrintToChat);
                var rank = 1;
                foreach (var entry in top)
                {
                    Helpers.WriteNewlineDelimited(
                        Translator.Instance["top.entry", rank, entry.Name, entry.Elo, entry.RankedRoundsPlayed],
                        current.PrintToChat);
                    rank++;
                }

                if (top.Count == 0)
                {
                    Helpers.WriteNewlineDelimited(Translator.Instance["top.empty"], current.PrintToChat);
                }
            });
        });
    }

    #endregion

    #region Test commands (admin)

    public void OnRankedForceCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        _testBypassMinPlayers = true;
        _ranked.ForceActivate();
        Helpers.PrintToAll(Translator.Instance["ranked.test_forced"]);
    }

    public void OnRankedForceStopCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        _testBypassMinPlayers = false;
        _ranked.ForceDeactivate();
        AnnounceRankedDeactivated();
    }

    public void OnRankedStateCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        var lines = new[]
        {
            $"State: {_ranked.State} | test bypass: {_testBypassMinPlayers}",
            $"Team humans: {Helpers.CountTeamHumanPlayers()} (min {Configs.GetConfigData().Ranked.MinPlayers})",
            $"Vote: initiator {_ranked.VoteInitiator}, accepted {_ranked.Accepted.Count}/{_ranked.EligibleVoters.Count}, " +
            $"deadline in {(int) Math.Max(0, (_ranked.VoteDeadlineUtc - DateTime.UtcNow).TotalSeconds)}s",
            $"DB ready: {_dbReady} | season: {SeasonManager.Instance.ActiveSeasonName} (#{SeasonManager.Instance.ActiveSeasonId})",
            $"Collecting round: {_collector.IsCollecting} | round ranked: {_collector.Context.IsRanked} | " +
            $"round type: {_collector.Context.RoundType}",
            $"Warmup hold: {_warmupHoldActive} | warmup: {Helpers.IsWarmup()}",
        };

        foreach (var line in lines)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}{line}");
        }
    }

    public void OnSetEloCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        if (!Helpers.IsHumanPlayer(player) || !_dbReady)
        {
            return;
        }

        if (commandInfo.ArgCount < 2 || !int.TryParse(commandInfo.GetArg(1), out var elo))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Invalid ELO value.");
            return;
        }

        elo = Math.Clamp(elo, Configs.GetConfigData().Elo.MinElo, Configs.GetConfigData().Elo.MaxElo);
        var steamId = Helpers.GetSteamId(player);
        var seasonId = SeasonManager.Instance.ActiveSeasonId;

        Task.Run(() =>
        {
            try
            {
                Queries.SetElo(seasonId, steamId, elo);
                Server.NextFrame(() =>
                {
                    _eloCache[steamId] = elo;
                    var current = Utilities.GetPlayers()
                        .FirstOrDefault(p => Helpers.IsHumanPlayer(p) && Helpers.GetSteamId(p) == steamId);
                    if (current is not null)
                    {
                        Helpers.WriteNewlineDelimited(
                            Translator.Instance["test.elo_set", elo], current.PrintToChat);
                    }
                });
            }
            catch (Exception e)
            {
                Log.Error($"css_rr_setelo failed: {e}");
            }
        });
    }

    public void OnTestRoundCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        if (!Helpers.IsHumanPlayer(player) || !_dbReady)
        {
            return;
        }

        var steamId = Helpers.GetSteamId(player);
        if (steamId == 0)
        {
            return;
        }

        var win = commandInfo.ArgCount <= 1 ||
                  !commandInfo.GetArg(1).Equals("loss", StringComparison.OrdinalIgnoreCase);
        var kills = 2;
        if (commandInfo.ArgCount > 2 && int.TryParse(commandInfo.GetArg(2), out var parsedKills))
        {
            kills = Math.Clamp(parsedKills, 0, 5);
        }

        var myTeam = player!.Team is CsTeam.CounterTerrorist ? TeamNums.Ct : TeamNums.T;
        var enemyTeam = myTeam == TeamNums.T ? TeamNums.Ct : TeamNums.T;
        var startingElo = Configs.GetConfigData().Elo.StartingElo;

        var ctx = new RoundContext
        {
            Map = Server.MapName,
            StartedAtUtc = DateTime.UtcNow,
            RoundType = RetakesRoundType.FullBuy,
            IsRanked = true,
            TPlayerCount = 1,
            CtPlayerCount = 1,
            WinnerTeamNum = win ? myTeam : enemyTeam,
            RoundDurationSeconds = 30,
        };

        var me = new PlayerRoundStats
        {
            SteamId = steamId,
            PlayerName = player.PlayerName,
            TeamNum = myTeam,
            EloBefore = _eloCache.TryGetValue(steamId, out var elo) ? elo : startingElo,
            Kills = kills,
            Headshots = kills / 2,
            Damage = kills * 90 + 20,
            Died = !win,
            WonRound = win,
        };

        const ulong testOpponentId = 999999999;
        var foe = new PlayerRoundStats
        {
            SteamId = testOpponentId,
            PlayerName = "TEST_BOT",
            TeamNum = enemyTeam,
            EloBefore = startingElo,
            Kills = win ? 0 : 2,
            Damage = win ? 20 : 180,
            Died = win,
            WonRound = !win,
        };

        var players = new List<PlayerRoundStats> {me, foe};
        EloEngine.FlagEarlyDeaths(players);
        RatingEngine.ComputeRatings(players, ctx);
        EloEngine.ApplyEloDeltas(players, ctx);

        Helpers.WriteNewlineDelimited(
            Translator.Instance["test.round_simulated",
                win ? "WIN" : "LOSS",
                me.Rating.ToString("0.00"),
                me.EloDelta >= 0 ? $"+{me.EloDelta}" : me.EloDelta.ToString(),
                me.EloAfter],
            player.PrintToChat);

        PersistRoundAsync(ctx, players);
    }

    #endregion

    #region Admin commands

    public void OnNewSeasonCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        if (!_dbReady)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Database not ready yet.");
            return;
        }

        var name = commandInfo.ArgCount > 1 ? commandInfo.GetArg(1) : null;

        Task.Run(() =>
        {
            try
            {
                var season = SeasonManager.Instance.StartNewSeason(name);
                Server.NextFrame(() => OnSeasonChanged(season.Name));
            }
            catch (Exception e)
            {
                Log.Error($"Failed to start new season: {e}");
            }
        });
    }

    public void OnSeasonsCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        if (!_dbReady)
        {
            return;
        }

        Task.Run(() =>
        {
            var seasons = Queries.GetAllSeasons();
            Server.NextFrame(() =>
            {
                foreach (var season in seasons)
                {
                    var line = $"#{season.Id} {season.Name} — {season.StartedAtUtc:yyyy-MM-dd}" +
                               (season.IsActive ? " (active)" : $" → {season.EndedAtUtc:yyyy-MM-dd}");
                    if (player is not null && player.IsValid)
                    {
                        player.PrintToChat($"{MessagePrefix}{line}");
                    }
                    else
                    {
                        Log.Info(line);
                    }
                }
            });
        });
    }

    public void OnReloadConfigCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        Configs.Load(_plugin.ModuleDirectory);
        commandInfo.ReplyToCommand($"{MessagePrefix}Config reloaded for version {PluginInfo.Version}.");
    }

    public void OnAutoScrambleCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        var cfg = Configs.GetConfigData().ModeCvars;
        cfg.ScrambleTeamsEachRound = !cfg.ScrambleTeamsEachRound;
        Configs.Save();

        var status = cfg.ScrambleTeamsEachRound ? "enabled" : "disabled";
        Helpers.PrintToAll($"{MessagePrefix}Auto team scramble every round is now {status}.");
    }

    public void OnDmgCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null) return;
        var steamId = Helpers.GetSteamId(player);
        if (steamId == 0) return;

        if (_optOutDamageReport.Contains(steamId))
        {
            _optOutDamageReport.Remove(steamId);
            player.PrintToChat($"{MessagePrefix}Damage report \x06enabled\x01.");
        }
        else
        {
            _optOutDamageReport.Add(steamId);
            player.PrintToChat($"{MessagePrefix}Damage report \x02disabled\x01.");
        }
    }

    #endregion
}
