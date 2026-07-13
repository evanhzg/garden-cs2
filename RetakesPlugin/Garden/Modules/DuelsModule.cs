using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRetakes.Core.Admin;
using GardenRetakes.Core.GameModes;
using RetakesPlugin.Models;
using RetakesPlugin.Utils;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// Duels mode v2 (ROADMAP R4 + R8): 1v1s on NAMED arenas with parallel lanes
/// and player challenges.
///
///  - Arenas: named pairs edited with !garena (new/setb/list/del), stored in
///    duels/&lt;map&gt;.json. Fallback: auto-pairing of "duel"-flagged spawns
///    (named "Arena N") when no named arena exists on the map.
///  - Parallel duels: up to min(arenas, Duels.MaxParallelDuels) lanes run at
///    once; one shared FIFO queue rotates losers out when someone waits.
///  - Challenges: !duel &lt;player&gt; [firstTo] reserves a private lane for both —
///    no rotation, own score, ends at first-to-X (or !duel stop when infinite).
/// </summary>
public class DuelsModule : IGardenModule
{
    public const string SpawnFlag = "duel";
    private const double InviteTimeoutSeconds = 30;

    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly AdminModule _admin;
    private readonly Random _random = new();
    private readonly DuelManager _manager = new();
    private readonly DuelArenaStore _arenaStore = new();

    private sealed record RuntimeArena(string Name, Spawn EndA, Spawn EndB);

    private List<RuntimeArena> _arenas = [];
    private string _mapName = "";

    // laneId -> arena index; laneId -> last started pair (to detect changes).
    private readonly Dictionary<int, int> _laneArena = new();
    private readonly Dictionary<int, (ulong A, ulong B)> _lastPairs = new();

    // R10: per-arena stats — arena name -> (steamid -> wins there).
    private readonly Dictionary<string, Dictionary<ulong, int>> _arenaWins = new();

    // Pending challenge invites: target -> (challenger, firstTo, deadline).
    private readonly Dictionary<ulong, (ulong Challenger, int? FirstTo, DateTime DeadlineUtc)> _invites = new();

    public string Name => "Duels";
    public bool Enabled => _host.Settings.Duels.Enabled;

    public DuelsModule(RetakesPlugin plugin, GardenHost host, AdminModule admin)
    {
        _plugin = plugin;
        _host = host;
        _admin = admin;
    }

    private bool IsActive => _host.Modes.CurrentMode == GameModeKind.Duels;

    /// <summary>R11: the edit mode edits arenas through the same store.</summary>
    internal DuelArenaStore ArenaStore => _arenaStore;

    internal void SaveArenas() => SaveArenaStore();

    private string Prefix => _plugin.Localizer["garden.prefix"];

    private string ArenaStorePath => Path.Combine(_plugin.ModuleDirectory, "duels", $"{_mapName}.json");

    public void Load(bool hotReload)
    {
        _host.Modes.ModeChanged += OnModeChanged;

        _plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        _plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        _plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        _plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        _plugin.AddCommand("css_duelscore", "Show the duel scoreboard.", OnDuelScoreCommand);
        _plugin.AddCommand("css_duel", "Challenge a player: !duel <player> [firstTo] | accept | decline | stop", OnDuelCommand);
        _plugin.AddCommand("css_garena", "Duel arena editor: new <name> | setb <name> | seta <name> | list | del <name>", OnArenaCommand);
    }

    public void OnMapStart(string mapName)
    {
        _mapName = mapName;
        _manager.Reset();
        _arenas = [];
        _laneArena.Clear();
        _lastPairs.Clear();
        _invites.Clear();
        LoadArenaStore();
    }

    public void Unload()
    {
        _host.Modes.ModeChanged -= OnModeChanged;
    }

    // ---------- arena data ----------

    private void LoadArenaStore()
    {
        _arenaStore.Clear();
        try
        {
            if (File.Exists(ArenaStorePath))
            {
                _arenaStore.Load(File.ReadAllText(ArenaStorePath));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/Duels", $"Failed to load {ArenaStorePath}: {ex.Message}");
        }
    }

    private void SaveArenaStore()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ArenaStorePath)!);
            File.WriteAllText(ArenaStorePath, _arenaStore.Serialize());
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/Duels", $"Failed to save {ArenaStorePath}: {ex.Message}");
        }
    }

    private static Spawn ToSpawn(ExecutePosition position) =>
        new(new Vector(position.X, position.Y, position.Z), new QAngle(position.Pitch, position.Yaw, 0));

    /// <summary>Named arenas first; fallback to proximity-paired "duel"-flagged spawns.</summary>
    private void BuildArenas()
    {
        _arenas = _arenaStore.Complete
            .Select(a => new RuntimeArena(a.Name, ToSpawn(a.EndA!), ToSpawn(a.EndB!)))
            .ToList();

        if (_arenas.Count > 0)
        {
            return;
        }

        var duelSpawns = _plugin.MapConfigService?.GetSpawnsClone()
            .Where(s => s.Flags.Contains(SpawnFlag, StringComparer.OrdinalIgnoreCase))
            .ToList() ?? [];

        var arenaSpawns = duelSpawns
            .Select((s, i) => new DuelArenas.ArenaSpawn(i, s.Vector.X, s.Vector.Y, s.Vector.Z))
            .ToList();

        _arenas = DuelArenas.BuildArenas(arenaSpawns, _host.Settings.Duels.MaxPairDistance)
            .Select((pair, i) => new RuntimeArena($"Arena {i + 1}",
                duelSpawns[pair.SpawnIdA], duelSpawns[pair.SpawnIdB]))
            .ToList();
    }

    // ---------- mode lifecycle ----------

    private void OnModeChanged(GameModeKind from, GameModeKind to)
    {
        if (to == GameModeKind.Duels)
        {
            StartDuels();
        }
        else if (from == GameModeKind.Duels)
        {
            StopDuels();
        }
    }

    private void StartDuels()
    {
        BuildArenas();
        if (_arenas.Count == 0)
        {
            Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.duels.no_arenas", SpawnFlag]}");
            _host.Modes.TryChangeMode(GameModeKind.Retakes, out _);
            return;
        }

        _manager.Reset();
        _laneArena.Clear();
        _lastPairs.Clear();
        _invites.Clear();
        _arenaWins.Clear();
        _manager.MaxLanes = Math.Max(1, Math.Min(_arenas.Count, _host.Settings.Duels.MaxParallelDuels));

        foreach (var player in GetTeamHumans())
        {
            _manager.AddPlayer(player.SteamID);
        }

        foreach (var command in _host.Settings.Duels.StartCommands)
        {
            Server.ExecuteCommand(command);
        }

        Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.duels.started", _arenas.Count]}");
        Logger.LogInfo("Garden/Duels", $"Duels started: {_arenas.Count} arenas, {_manager.MaxLanes} lanes max.");
    }

    private void StopDuels()
    {
        _manager.Reset();
        _invites.Clear();
        foreach (var command in _host.Settings.Duels.StopCommands)
        {
            Server.ExecuteCommand(command);
        }

        Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.duels.stopped"]}");
    }

    // ---------- round flow ----------

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (!IsActive)
        {
            return HookResult.Continue;
        }

        _plugin.AddTimer(1.5f, () =>
        {
            if (!IsActive)
            {
                return;
            }

            ParkNonFighters();
            _lastPairs.Clear();
            SyncLanes();
        });

        return HookResult.Continue;
    }

    private void ParkNonFighters()
    {
        foreach (var player in GetTeamHumans())
        {
            if (_manager.LaneOf(player.SteamID) is null && player.PawnIsAlive)
            {
                player.PlayerPawn.Value?.CommitSuicide(explode: false, force: true);
            }
        }
    }

    /// <summary>(Re)starts every ready lane whose pair changed since last time.</summary>
    private void SyncLanes()
    {
        if (!IsActive)
        {
            return;
        }

        // Drop arena assignments of dead lanes.
        var liveIds = _manager.Lanes.Select(l => l.Id).ToHashSet();
        foreach (var stale in _laneArena.Keys.Where(id => !liveIds.Contains(id)).ToList())
        {
            _laneArena.Remove(stale);
            _lastPairs.Remove(stale);
        }

        foreach (var lane in _manager.Lanes.Where(l => l.IsReady))
        {
            var pair = (lane.FighterA!.Value, lane.FighterB!.Value);
            if (_lastPairs.TryGetValue(lane.Id, out var last) && last == pair)
            {
                continue;
            }

            _lastPairs[lane.Id] = pair;
            StartLaneDuel(lane);
        }
    }

    private void StartLaneDuel(DuelLane lane)
    {
        var fighterA = FindBySteamId(lane.FighterA!.Value);
        var fighterB = FindBySteamId(lane.FighterB!.Value);
        if (fighterA is null || fighterB is null)
        {
            if (fighterA is null) _manager.RemovePlayer(lane.FighterA!.Value);
            if (fighterB is null && lane.FighterB is not null) _manager.RemovePlayer(lane.FighterB.Value);
            _plugin.AddTimer(0.5f, SyncLanes);
            return;
        }

        var arenaIndex = PickArenaFor(lane);
        var arena = _arenas[arenaIndex];

        var (spawnA, spawnB) = _random.Next(2) == 0
            ? (arena.EndA, arena.EndB)
            : (arena.EndB, arena.EndA);

        if (fighterA.Team != CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist) fighterA.ChangeTeam(CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist);
        if (fighterB.Team != CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist) fighterB.ChangeTeam(CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist);

        SetupFighter(fighterA, spawnA);
        SetupFighter(fighterB, spawnB);

        // R10: point dead/queued players at the action.
        _plugin.AddTimer(0.5f, () => AutoSpectate(fighterA));

        if (lane.IsChallenge)
        {
            Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.duels.challenge_versus",
                fighterA.PlayerName, fighterB.PlayerName, arena.Name, lane.ScoreLine,
                lane.FirstTo is { } target ? $"first to {target}" : "infinite"]}");
        }
        else
        {
            Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.duels.versus",
                fighterA.PlayerName, fighterB.PlayerName, arena.Name]}");
        }
    }

    /// <summary>
    /// Challenge lanes keep their arena; normal lanes rotate to a random arena
    /// that no other lane currently uses.
    /// </summary>
    private int PickArenaFor(DuelLane lane)
    {
        if (_laneArena.TryGetValue(lane.Id, out var current) && lane.IsChallenge)
        {
            return current;
        }

        var used = _laneArena.Where(kv => kv.Key != lane.Id).Select(kv => kv.Value).ToHashSet();
        var free = Enumerable.Range(0, _arenas.Count).Where(i => !used.Contains(i)).ToList();
        if (free.Count == 0)
        {
            free = Enumerable.Range(0, _arenas.Count).ToList();
        }

        // Avoid an immediate repeat when another free arena exists.
        if (_laneArena.TryGetValue(lane.Id, out var previous) && free.Count > 1)
        {
            free.Remove(previous);
        }

        var chosen = free[_random.Next(free.Count)];
        _laneArena[lane.Id] = chosen;
        return chosen;
    }

    /// <summary>
    /// R10: dead/queued players spectate the duel that just started (in-eye on
    /// one fighter). Raw observer-handle writes are guarded — if a CS2 update
    /// breaks this, disable via Duels.SpectatorAutoFollow.
    /// </summary>
    private void AutoSpectate(CCSPlayerController fighter)
    {
        if (!IsActive || !_host.Settings.Duels.SpectatorAutoFollow ||
            !PlayerHelper.HasAlivePawn(fighter))
        {
            return;
        }

        var targetPawn = fighter.PlayerPawn.Value!;
        foreach (var player in GetTeamHumans())
        {
            if (player.PawnIsAlive || _manager.LaneOf(player.SteamID) is not null)
            {
                continue;
            }

            try
            {
                var pawn = player.PlayerPawn.Value;
                var observer = pawn?.ObserverServices;
                if (pawn is null || observer is null)
                {
                    continue;
                }

                observer.ObserverMode = (byte) ObserverMode_t.OBS_MODE_IN_EYE;
                observer.ObserverTarget.Raw = targetPawn.EntityHandle.Raw;
                Utilities.SetStateChanged(pawn, "CPlayer_ObserverServices", "m_iObserverMode");
                Utilities.SetStateChanged(pawn, "CPlayer_ObserverServices", "m_hObserverTarget");
            }
            catch (Exception ex)
            {
                Logger.LogDebug("Garden/Duels", $"AutoSpectate failed for {player.PlayerName}: {ex.Message}");
            }
        }
    }

    private void SetupFighter(CCSPlayerController player, Spawn spawn)
    {
        if (!player.PawnIsAlive)
        {
            player.Respawn();
        }

        Server.NextFrame(() =>
        {
            if (!PlayerHelper.HasAlivePawn(player))
            {
                return;
            }

            var pawn = player.PlayerPawn.Value!;
            pawn.Teleport(spawn.Vector, spawn.QAngle, new Vector(0, 0, 0));

            pawn.Health = 100;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");

            RetakesAllocator.Helpers.RemoveWeapons(player);
            foreach (var weapon in _host.Settings.Duels.Weapons)
            {
                player.GiveNamedItem(weapon);
            }

            if (_host.Settings.Duels.GiveKevlarHelmet)
            {
                player.GiveNamedItem("item_assaultsuit");
            }
        });
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!IsActive || @event.Userid is null)
        {
            return HookResult.Continue;
        }

        var result = _manager.OnDeath(@event.Userid.SteamID);
        if (result is null)
        {
            return HookResult.Continue;
        }

        // FIX: Force respawn for the lane even if the next fighters are the same.
        _lastPairs.Remove(result.Lane.Id);

        // R10: per-arena win stats.
        var duelArenaName = "";
        if (_laneArena.TryGetValue(result.Lane.Id, out var arenaIndex) && arenaIndex < _arenas.Count)
        {
            duelArenaName = _arenas[arenaIndex].Name;
            if (!_arenaWins.TryGetValue(duelArenaName, out var arenaBoard))
            {
                arenaBoard = _arenaWins[duelArenaName] = new Dictionary<ulong, int>();
            }

            arenaBoard[result.WinnerId] = arenaBoard.GetValueOrDefault(result.WinnerId) + 1;
        }

        var winnerName = FindBySteamId(result.WinnerId)?.PlayerName ?? "?";
        var loserName = FindBySteamId(result.LoserId)?.PlayerName ?? "?";

        // Persist the duel to the shared DB (best effort — feeds the /duels ladder).
        PersistDuel(result, winnerName, loserName, duelArenaName);
        if (result.Lane.IsChallenge)
        {
            if (result.ChallengeFinished)
            {
                Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.duels.challenge_won",
                    winnerName, result.Lane.ScoreLine]}");
            }
            else
            {
                Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.duels.challenge_score",
                    winnerName, result.Lane.ScoreLine]}");
            }
        }
        else
        {
            Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.duels.won",
                winnerName, _manager.Wins.GetValueOrDefault(result.WinnerId)]}");
        }

        _plugin.AddTimer(1.0f, SyncLanes);
        return HookResult.Continue;
    }

    /// <summary>
    /// Writes one completed duel to the shared DB (guarded — skipped when the
    /// rankings module/DB isn't available).
    /// </summary>
    private void PersistDuel(GardenRetakes.Core.GameModes.DuelManager.DuelResult result,
        string winnerName, string loserName, string arenaName)
    {
        if (!_host.Settings.Rankings.Enabled ||
            !GardenRankingsCore.Config.Configs.IsLoaded())
        {
            return;
        }

        var map = Server.MapName;
        var isChallenge = result.Lane.IsChallenge;
        var challengeScore = result.ChallengeFinished ? result.Lane.ScoreLine : "";
        var (winnerId, loserId) = (result.WinnerId, result.LoserId);

        Task.Run(() =>
        {
            try
            {
                var seasonId = GardenRankingsCore.Managers.SeasonManager.Instance.ActiveSeasonId;
                GardenRankingsCore.Db.Queries.PersistDuel(
                    seasonId, map, arenaName,
                    winnerId, winnerName, loserId, loserName,
                    isChallenge, challengeScore);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Garden/Duels", $"Failed to persist duel: {ex.Message}");
            }
        });
    }

    // ---------- player lifecycle ----------

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        if (!IsActive || @event.Userid is null ||
            !PlayerHelper.IsValid(@event.Userid) || @event.Userid.IsBot || @event.Userid.IsHLTV)
        {
            return HookResult.Continue;
        }

        var player = @event.Userid;
        Server.NextFrame(() =>
        {
            if (!IsActive || !PlayerHelper.IsValid(player))
            {
                return;
            }

            _manager.AddPlayer(player.SteamID);
            player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.duels.queued",
                _manager.Queue.Count]}");
            SyncLanes();
        });

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (!IsActive || @event.Userid is null)
        {
            return HookResult.Continue;
        }

        var steamId = @event.Userid.SteamID;
        var lane = _manager.LaneOf(steamId);
        if (lane is { IsChallenge: true })
        {
            Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.duels.challenge_cancelled"]}");
        }

        _manager.RemovePlayer(steamId);
        _invites.Remove(steamId);
        _plugin.AddTimer(0.5f, SyncLanes);
        return HookResult.Continue;
    }

    // ---------- challenge command ----------

    private void OnDuelCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsActive)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.not_active"]}");
            return;
        }

        if (!PlayerHelper.IsValid(player) || player!.IsBot)
        {
            return;
        }

        var arg = info.GetArg(1);
        switch (arg.ToLowerInvariant())
        {
            case "":
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.duel_usage"]}");
                return;
            case "accept" or "yes":
                AcceptInvite(player, info);
                return;
            case "decline" or "no":
            {
                if (_invites.Remove(player.SteamID, out var declined))
                {
                    var challenger = FindBySteamId(declined.Challenger);
                    challenger?.PrintToChat($"{Prefix} {_plugin.Localizer["garden.duels.invite_declined",
                        player.PlayerName]}");
                }

                return;
            }
            case "stop":
            {
                if (_manager.CancelChallenge(player.SteamID))
                {
                    Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.duels.challenge_cancelled"]}");
                    _plugin.AddTimer(0.5f, SyncLanes);
                }

                return;
            }
            default:
                CreateInvite(player, info, arg);
                return;
        }
    }

    private void CreateInvite(CCSPlayerController challenger, CommandInfo info, string targetName)
    {
        var target = GetTeamHumans().FirstOrDefault(p =>
            p.SteamID != challenger.SteamID &&
            p.PlayerName.Contains(targetName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.target_not_found", targetName]}");
            return;
        }

        if (_manager.LaneOf(challenger.SteamID) is { IsChallenge: true } ||
            _manager.LaneOf(target.SteamID) is { IsChallenge: true })
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.already_challenging"]}");
            return;
        }

        int? firstTo = null;
        if (int.TryParse(info.GetArg(2), out var parsed) && parsed > 0)
        {
            firstTo = Math.Min(parsed, 100);
        }

        _invites[target.SteamID] = (challenger.SteamID, firstTo, DateTime.UtcNow.AddSeconds(InviteTimeoutSeconds));

        var rules = firstTo is { } x
            ? _plugin.Localizer["garden.duels.rules_first_to", x]
            : _plugin.Localizer["garden.duels.rules_infinite"];
        target.PrintToChat($"{Prefix} {_plugin.Localizer["garden.duels.invited",
            challenger.PlayerName, rules]}");
        info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.invite_sent",
            target.PlayerName]}");
    }

    private void AcceptInvite(CCSPlayerController player, CommandInfo info)
    {
        if (!_invites.Remove(player.SteamID, out var invite) ||
            DateTime.UtcNow > invite.DeadlineUtc)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.no_invite"]}");
            return;
        }

        var challenger = FindBySteamId(invite.Challenger);
        if (challenger is null)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.no_invite"]}");
            return;
        }

        _manager.StartChallenge(invite.Challenger, player.SteamID, invite.FirstTo);
        var rules = invite.FirstTo is { } x
            ? _plugin.Localizer["garden.duels.rules_first_to", x]
            : _plugin.Localizer["garden.duels.rules_infinite"];
        Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.duels.challenge_started",
            challenger.PlayerName, player.PlayerName, rules]}");
        _plugin.AddTimer(0.5f, SyncLanes);
    }

    // ---------- arena editor ----------

    private void OnArenaCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!_admin.Require(player, info, AdminLevel.Admin) || !PlayerHelper.IsValid(player))
        {
            return;
        }

        var action = info.GetArg(1).ToLowerInvariant();
        // R11: multi-word arena names ("A Site VS Long").
        var name = info.ArgCount > 2
            ? string.Join(' ', Enumerable.Range(2, info.ArgCount - 2).Select(info.GetArg)).Trim()
            : "";

        ExecutePosition? HereOrNull()
        {
            if (!PlayerHelper.HasAlivePawn(player))
            {
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.need_alive"]}");
                return null;
            }

            var pawn = player!.PlayerPawn.Value!;
            return new ExecutePosition
            {
                X = pawn.AbsOrigin!.X, Y = pawn.AbsOrigin.Y, Z = pawn.AbsOrigin.Z,
                Pitch = pawn.AbsRotation!.X, Yaw = pawn.AbsRotation.Y,
            };
        }

        switch (action)
        {
            case "new":
            {
                if (!_arenaStore.TryAdd(name, player!.PlayerName, out var error))
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.arena_error", error ?? "?"]}");
                    return;
                }

                var here = HereOrNull();
                if (here is not null)
                {
                    _arenaStore.Find(name)!.EndA = here;
                }

                SaveArenaStore();
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.arena_created", name]}");
                return;
            }
            case "seta" or "setb":
            {
                var arena = _arenaStore.Find(name);
                if (arena is null)
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.arena_not_found", name]}");
                    return;
                }

                var here = HereOrNull();
                if (here is null)
                {
                    return;
                }

                if (action == "seta") arena.EndA = here;
                else arena.EndB = here;
                SaveArenaStore();
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.arena_end_set",
                    action == "seta" ? "A" : "B", name, arena.IsComplete ? "✔" : "…"]}");
                return;
            }
            case "del":
            {
                if (!_arenaStore.Remove(name))
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.arena_not_found", name]}");
                    return;
                }

                SaveArenaStore();
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.arena_deleted", name]}");
                return;
            }
            case "list":
            {
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.arena_list_header", _mapName]}");
                foreach (var arena in _arenaStore.Arenas)
                {
                    info.ReplyToCommand($"{Prefix} {arena.Name} — {(arena.IsComplete ? "complete" : "incomplete")} " +
                                        $"(by {arena.AddedBy ?? "?"})");
                }

                return;
            }
            default:
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.arena_usage"]}");
                return;
        }
    }

    private void OnDuelScoreCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsActive)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.not_active"]}");
            return;
        }

        // R10: "!duelscore arenas" shows per-arena stats.
        if (info.GetArg(1).Equals("arenas", StringComparison.OrdinalIgnoreCase))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.arena_score_header"]}");
            foreach (var (arenaName, board) in _arenaWins.OrderByDescending(a => a.Value.Values.Sum()))
            {
                var total = board.Values.Sum();
                var best = board.OrderByDescending(b => b.Value).First();
                var bestName = FindBySteamId(best.Key)?.PlayerName ?? best.Key.ToString();
                info.ReplyToCommand($"{Prefix} {arenaName} — {total} duels (best: {bestName}, {best.Value})");
            }

            if (_arenaWins.Count == 0)
            {
                info.ReplyToCommand($"{Prefix} —");
            }

            return;
        }

        info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.score_header"]}");
        foreach (var (steamId, wins) in _manager.Wins.OrderByDescending(w => w.Value).Take(10))
        {
            var name = FindBySteamId(steamId)?.PlayerName ?? steamId.ToString();
            info.ReplyToCommand($"{Prefix} {name} — {wins}");
        }
    }

    // ---------- helpers ----------

    private static List<CCSPlayerController> GetTeamHumans() =>
        Utilities.GetPlayers()
            .Where(p => PlayerHelper.IsValid(p) && !p.IsBot && !p.IsHLTV &&
                        p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            .ToList();

    private static CCSPlayerController? FindBySteamId(ulong steamId) =>
        Utilities.GetPlayers().FirstOrDefault(p =>
            PlayerHelper.IsValid(p) && !p.IsBot && p.SteamID == steamId);
}
