using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRetakes.Core.Admin;
using GardenRetakes.Core.GameModes;
using RetakesPlugin.Utils;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// Executes mode (ROADMAP R6): Ts execute onto a bombsite with predefined
/// positioning and utility auto-thrown at round start; CTs hold a predefined
/// defensive setup. Normal win conditions (plant/defuse/elimination).
///
/// Strategies are stored per map in executes/&lt;map&gt;.json and edited in game:
///   !gexec new &lt;name&gt; &lt;a|b&gt;    create a strategy   (Admin)
///   !gexec edit &lt;name&gt;         select it for editing
///   !gexec tstart / ctsetup    add a position where you stand (with your angles)
///   !gexec nade [delay]        save your LAST thrown grenade as an auto-throw
///   !gexec info [name] / list / del &lt;name&gt; / play &lt;name&gt; / random
/// Lineups are captured from real throws: throw the nade in game, then !gexec nade.
///
/// NOTE: server-side projectile spawning is engine-sensitive; smokes use the
/// native CXGrenadeProjectile::Create() via GrenadeFunctions (smoke/HE) for
/// proper trajectory + auto-detonation, with CreateEntityByName fallback
/// for molotov/flash. If a type misbehaves after a CS2 update, update the
/// byte-pattern signatures in GrenadeFunctions.cs first.
/// </summary>
public class ExecutesModule : IGardenModule
{
    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly AdminModule _admin;
    private readonly Random _random = new();
    private readonly ExecuteStore _store = new();

    private string _mapName = "";
    private string? _forcedStrategy;
    private ExecuteStrategy? _current;

    // Editing sessions: admin steamid -> strategy name being edited.
    private readonly Dictionary<ulong, string> _editing = new();

    // Last thrown grenade per player (captured while at least one admin edits).
    private sealed record CapturedThrow(UtilityType Type, string Team, float X, float Y, float Z,
        float VelX, float VelY, float VelZ);

    private readonly Dictionary<ulong, CapturedThrow> _lastThrow = new();



    public string Name => "Executes";
    public bool Enabled => _host.Settings.Executes.Enabled;

    public ExecutesModule(RetakesPlugin plugin, GardenHost host, AdminModule admin)
    {
        _plugin = plugin;
        _host = host;
        _admin = admin;
    }

    private bool IsActive => _host.Modes.CurrentMode == GameModeKind.Executes;

    /// <summary>R7: Fast-strat reuses the same strategy store and helpers.</summary>
    public ExecuteStore Store => _store;

    /// <summary>R11: the edit mode captures throws too, and saves through us.</summary>
    internal bool CaptureAllThrows { get; set; }

    internal void Save() => SaveStore();

    /// <summary>R11: last captured throw of a player as a ready-to-add UtilityThrow.</summary>
    internal bool TryGetLastThrow(ulong steamId, out UtilityThrow utility)
    {
        if (_lastThrow.TryGetValue(steamId, out var captured))
        {
            utility = new UtilityThrow
            {
                Type = captured.Type,
                Team = captured.Team,
                X = captured.X, Y = captured.Y, Z = captured.Z,
                VelX = captured.VelX, VelY = captured.VelY, VelZ = captured.VelZ,
                DelaySeconds = 0.5f,
            };
            return true;
        }

        utility = new UtilityThrow();
        return false;
    }

    private string Prefix => _plugin.Localizer["garden.prefix"];

    private string StorePath => Path.Combine(_plugin.ModuleDirectory, "executes", $"{_mapName}.json");

    public void Load(bool hotReload)
    {
        _host.Modes.ModeChanged += OnModeChanged;

        _plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        _plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        _plugin.RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);

        _plugin.AddCommand("css_gexec", "Executes strategies: new/edit/tstart/ctsetup/nade/list/info/del/play/random.", OnGExecCommand);
    }


    public void OnMapStart(string mapName)
    {
        _mapName = mapName;
        _forcedStrategy = null;
        _current = null;
        _editing.Clear();
        _lastThrow.Clear();
        LoadStore();
    }

    public void Unload()
    {
        _host.Modes.ModeChanged -= OnModeChanged;
    }

    // ---------- persistence ----------

    private void LoadStore()
    {
        _store.Clear();
        try
        {
            if (File.Exists(StorePath))
            {
                _store.Load(File.ReadAllText(StorePath));
                Logger.LogInfo("Garden/Executes", $"Loaded {_store.Strategies.Count} strategies for {_mapName}.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/Executes", $"Failed to load {StorePath}: {ex.Message}");
        }
    }

    private void SaveStore()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, _store.Serialize());
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/Executes", $"Failed to save {StorePath}: {ex.Message}");
        }
    }

    // ---------- mode lifecycle ----------

    private void OnModeChanged(GameModeKind from, GameModeKind to)
    {
        if (to == GameModeKind.Executes)
        {
            if (_store.Playable.Count == 0)
            {
                Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.exec.no_strategies"]}");
                _host.Modes.TryChangeMode(GameModeKind.Retakes, out _);
                return;
            }

            foreach (var command in _host.Settings.Executes.StartCommands)
            {
                Server.ExecuteCommand(command);
            }

            Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.exec.started", _store.Playable.Count]}");
        }
        else if (from == GameModeKind.Executes)
        {
            _current = null;
            _forcedStrategy = null;
            foreach (var command in _host.Settings.Executes.StopCommands)
            {
                Server.ExecuteCommand(command);
            }

            Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.exec.stopped"]}");
        }
    }

    // ---------- round flow ----------

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (!IsActive)
        {
            return HookResult.Continue;
        }

        _current = _forcedStrategy is not null
            ? _store.Find(_forcedStrategy)
            : _store.PickRandom(_random);

        if (_current is null || !_current.IsPlayable)
        {
            _current = _store.PickRandom(_random);
        }

        if (_current is null)
        {
            Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.exec.no_strategies"]}");
            return HookResult.Continue;
        }

        // Position everyone during freeze time (players just spawned at map defaults).
        _plugin.AddTimer(0.3f, SetupCurrentStrategy);

        return HookResult.Continue;
    }

    private void SetupCurrentStrategy()
    {
        if (!IsActive || _current is null)
        {
            return;
        }

        var cfg = _host.Settings.Executes;
        var ts = GetAliveTeam(CsTeam.Terrorist);
        var cts = GetAliveTeam(CsTeam.CounterTerrorist);

        PlaceGroup(ts, _current.TStarts, cfg.TWeapons, cfg.GiveKevlarHelmet);
        PlaceGroup(cts, _current.CtSetups, cfg.CtWeapons, cfg.GiveKevlarHelmet);

        // The execute needs a bomb.
        var bombCarrier = ts.FirstOrDefault();
        bombCarrier?.GiveNamedItem("weapon_c4");

        Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.exec.playing",
            _current.Name, _current.Site, _current.Utilities.Count]}");
    }

    internal void PlaceGroup(List<CCSPlayerController> players, List<ExecutePosition> positions,
        List<string> weapons, bool armor)
    {
        if (positions.Count == 0)
        {
            return;
        }

        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            var position = positions[i % positions.Count];
            // Stacked players (more players than positions) get a small offset.
            var offset = 40f * (i / positions.Count);

            var pawn = player.PlayerPawn.Value;
            if (pawn is null)
            {
                continue;
            }

            pawn.Teleport(
                new Vector(position.X + offset, position.Y, position.Z),
                new QAngle(position.Pitch, position.Yaw, 0),
                new Vector(0, 0, 0));

            RetakesAllocator.Helpers.RemoveWeapons(player);
            foreach (var weapon in weapons)
            {
                player.GiveNamedItem(weapon);
            }

            if (armor)
            {
                player.GiveNamedItem("item_assaultsuit");
            }
        }
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (!IsActive || _current is null)
        {
            return HookResult.Continue;
        }

        foreach (var utility in _current.Utilities)
        {
            var toThrow = utility;
            _plugin.AddTimer(Math.Max(0.1f, toThrow.DelaySeconds), () => ThrowUtility(toThrow));
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Spawns a grenade projectile with the recorded position + velocity.
    /// Smoke/HE use the game's native CXGrenadeProjectile::Create() via
    /// GrenadeFunctions — these fly the real trajectory and detonate properly.
    /// Molotov/flash (and signature failures) fall back to CreateEntityByName.
    /// </summary>
    internal void ThrowUtility(UtilityThrow utility)
    {
        // Also used by Fast-strat (R7) and by the !gedit nade preview (Edit mode).
        if (_host.Modes.CurrentMode is not (GameModeKind.Executes or GameModeKind.FastStrat or GameModeKind.Edit))
        {
            return;
        }

        var position = new Vector(utility.X, utility.Y, utility.Z);
        var velocity = new Vector(utility.VelX, utility.VelY, utility.VelZ);
        var angle = new QAngle(0, 0, 0);
        // R10: throw as the side that recorded the lineup (old data defaults to T).
        var team = utility.Team.Equals("CT", StringComparison.OrdinalIgnoreCase)
            ? CsTeam.CounterTerrorist
            : CsTeam.Terrorist;

        // A grenade only flies + detonates when attributed to a live pawn.
        var thrower = PickThrower(team);
        if (thrower is null || thrower.PlayerPawn.Value is null)
        {
            return;
        }

        try
        {
            var pawnPtr = thrower.PlayerPawn.Value.Handle;

            // --- Try native Create for Smoke / HE (proper trajectory + detonation) ---
            CBaseCSGrenadeProjectile? projectile = null;

            if (utility.Type == UtilityType.Smoke && GrenadeFunctions.Smoke is not null)
            {
                try
                {
                    var angVel = new Vector(velocity.X, velocity.Y, velocity.Z);
                    projectile = GrenadeFunctions.Smoke.Invoke(
                        position.Handle, angle.Handle,
                        velocity.Handle, angVel.Handle,
                        pawnPtr, 0, (int) team);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Garden/Executes", $"Native smoke Create failed, using fallback: {ex.Message}");
                    projectile = null;
                }
            }
            else if (utility.Type == UtilityType.HE && GrenadeFunctions.He is not null)
            {
                try
                {
                    var angVel = new Vector(velocity.X, velocity.Y, velocity.Z);
                    projectile = GrenadeFunctions.He.Invoke(
                        position.Handle, angle.Handle,
                        velocity.Handle, angVel.Handle,
                        pawnPtr, 0);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Garden/Executes", $"Native HE Create failed, using fallback: {ex.Message}");
                    projectile = null;
                }
            }

            // --- Fallback: CreateEntityByName (molotov, flash, or failed native) ---
            if (projectile is null || !projectile.IsValid)
            {
                projectile = utility.Type switch
                {
                    UtilityType.Smoke => Utilities.CreateEntityByName<CSmokeGrenadeProjectile>("smokegrenade_projectile"),
                    UtilityType.Molotov => Utilities.CreateEntityByName<CMolotovProjectile>("molotov_projectile"),
                    UtilityType.HE => Utilities.CreateEntityByName<CHEGrenadeProjectile>("hegrenade_projectile"),
                    UtilityType.Flash => Utilities.CreateEntityByName<CFlashbangProjectile>("flashbang_projectile"),
                    _ => null,
                };

                if (projectile is null || !projectile.IsValid)
                {
                    Logger.LogWarning("Garden/Executes", $"ThrowUtility: {utility.Type} create returned null.");
                    return;
                }

                // CreateEntityByName path: wire everything up manually.
                projectile.DispatchSpawn();

                projectile.InitialPosition.X = position.X;
                projectile.InitialPosition.Y = position.Y;
                projectile.InitialPosition.Z = position.Z;
                projectile.InitialVelocity.X = velocity.X;
                projectile.InitialVelocity.Y = velocity.Y;
                projectile.InitialVelocity.Z = velocity.Z;
                projectile.AngVelocity.X = velocity.X;
                projectile.AngVelocity.Y = velocity.Y;
                projectile.AngVelocity.Z = velocity.Z;
                projectile.Teleport(position, angle, velocity);
                projectile.Globalname = "custom";
                projectile.TeamNum = (byte) team;
                projectile.Thrower.Raw = thrower.PlayerPawn.Raw;
                projectile.OriginalThrower.Raw = thrower.PlayerPawn.Raw;
                projectile.OwnerEntity.Raw = thrower.PlayerPawn.Raw;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/Executes", $"ThrowUtility({utility.Type}) failed: {ex.Message}");
        }
    }

    private static CCSPlayerController? PickThrower(CsTeam preferredTeam)
    {
        var alive = Utilities.GetPlayers()
            .Where(p => PlayerHelper.IsValid(p) && !p.IsBot && p.PawnIsAlive &&
                        p.PlayerPawn.Value is not null)
            .ToList();

        return alive.FirstOrDefault(p => p.Team == preferredTeam) ?? alive.FirstOrDefault();
    }

    // ---------- lineup capture ----------

    private void OnEntitySpawned(CEntityInstance entity)
    {
        if (_editing.Count == 0 && !CaptureAllThrows)
        {
            return;
        }

        var type = entity.DesignerName switch
        {
            "smokegrenade_projectile" => UtilityType.Smoke,
            "flashbang_projectile" => UtilityType.Flash,
            "hegrenade_projectile" => UtilityType.HE,
            "molotov_projectile" => UtilityType.Molotov,
            _ => (UtilityType?) null,
        };

        if (type is null)
        {
            return;
        }

        var index = (int) entity.Index;
        // Position/velocity/thrower are settled on the next frame.
        Server.NextFrame(() =>
        {
            var projectile = Utilities.GetEntityFromIndex<CBaseCSGrenadeProjectile>(index);
            if (projectile is null || !projectile.IsValid ||
                projectile.AbsOrigin is null || projectile.AbsVelocity is null)
            {
                return;
            }

            var thrower = projectile.Thrower.Value?.Controller.Value?.As<CCSPlayerController>();
            if (thrower is null || !thrower.IsValid)
            {
                return;
            }

            // R10: the lineup belongs to the side the thrower was on.
            var team = thrower.Team == CsTeam.CounterTerrorist ? "CT" : "T";
            _lastThrow[thrower.SteamID] = new CapturedThrow(type.Value, team,
                projectile.AbsOrigin.X, projectile.AbsOrigin.Y, projectile.AbsOrigin.Z,
                projectile.AbsVelocity.X, projectile.AbsVelocity.Y, projectile.AbsVelocity.Z);
        });
    }

    // ---------- commands ----------

    private void OnGExecCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!_admin.Require(player, info, AdminLevel.Admin) || !PlayerHelper.IsValid(player))
        {
            return;
        }

        var action = info.GetArg(1).ToLowerInvariant();
        // R11: multi-word names — everything after the action (or after the site
        // for "new") is the strategy name.
        var arg = info.ArgCount > 2
            ? string.Join(' ', Enumerable.Range(2, info.ArgCount - 2).Select(info.GetArg)).Trim()
            : "";

        switch (action)
        {
            case "new":
            {
                // R11: signature is now "new <a|b> <name...>".
                var site = info.GetArg(2);
                var newName = info.ArgCount > 3
                    ? string.Join(' ', Enumerable.Range(3, info.ArgCount - 3).Select(info.GetArg)).Trim()
                    : "";
                if (!_store.TryAdd(newName, site, player!.PlayerName, out var error))
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.error", error ?? "?"]}");
                    return;
                }

                SaveStore();
                _editing[player.SteamID] = newName;
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.created", newName,
                    site.ToUpperInvariant()]}");
                return;
            }
            case "edit":
            {
                if (_store.Find(arg) is null)
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.not_found", arg]}");
                    return;
                }

                _editing[player!.SteamID] = arg;
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.editing", arg]}");
                return;
            }
            case "tstart" or "ctsetup":
            {
                if (!TryGetEdited(player!, info, out var strategy) ||
                    !PlayerHelper.HasAlivePawn(player))
                {
                    return;
                }

                var pawn = player!.PlayerPawn.Value!;
                var position = new ExecutePosition
                {
                    X = pawn.AbsOrigin!.X, Y = pawn.AbsOrigin.Y, Z = pawn.AbsOrigin.Z,
                    Pitch = pawn.AbsRotation!.X, Yaw = pawn.AbsRotation.Y,
                };

                (action == "tstart" ? strategy!.TStarts : strategy!.CtSetups).Add(position);
                SaveStore();
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.position_added",
                    action == "tstart" ? "T" : "CT", strategy.Name,
                    strategy.TStarts.Count, strategy.CtSetups.Count]}");
                return;
            }
            case "nade":
            {
                if (!TryGetEdited(player!, info, out var strategy))
                {
                    return;
                }

                if (!_lastThrow.TryGetValue(player!.SteamID, out var captured))
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.no_throw"]}");
                    return;
                }

                var delay = 0.5f;
                if (float.TryParse(arg, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsedDelay))
                {
                    delay = Math.Clamp(parsedDelay, 0.1f, 20f);
                }

                strategy!.Utilities.Add(new UtilityThrow
                {
                    Type = captured.Type,
                    Team = captured.Team,
                    X = captured.X, Y = captured.Y, Z = captured.Z,
                    VelX = captured.VelX, VelY = captured.VelY, VelZ = captured.VelZ,
                    DelaySeconds = delay,
                });
                SaveStore();
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.nade_added",
                    $"{captured.Team} {captured.Type}", strategy.Name, delay.ToString("0.0"),
                    strategy.Utilities.Count]}");
                return;
            }
            case "weight":
            {
                if (!TryGetEdited(player!, info, out var strategy))
                {
                    return;
                }

                if (!int.TryParse(arg, out var weight) || weight < 0 || weight > 100)
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.weight_usage"]}");
                    return;
                }

                strategy!.Weight = weight;
                SaveStore();
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.weight_set",
                    strategy.Name, weight]}");
                return;
            }
            case "del":
            {
                if (!_store.Remove(arg))
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.not_found", arg]}");
                    return;
                }

                SaveStore();
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.deleted", arg]}");
                return;
            }
            case "list":
            {
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.list_header", _mapName]}");
                foreach (var strategy in _store.Strategies)
                {
                    info.ReplyToCommand($"{Prefix} {strategy.Name} @{strategy.Site} — " +
                        $"T:{strategy.TStarts.Count} CT:{strategy.CtSetups.Count} " +
                        $"util:{strategy.Utilities.Count}{(strategy.IsPlayable ? "" : " (incomplete)")}");
                }

                return;
            }
            case "info":
            {
                var strategy = _store.Find(string.IsNullOrWhiteSpace(arg)
                    ? _editing.GetValueOrDefault(player!.SteamID) ?? ""
                    : arg);
                if (strategy is null)
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.not_found", arg]}");
                    return;
                }

                info.ReplyToCommand($"{Prefix} {strategy.Name} @{strategy.Site} by {strategy.AddedBy ?? "?"} — " +
                    $"T:{strategy.TStarts.Count} CT:{strategy.CtSetups.Count} util:{strategy.Utilities.Count} " +
                    $"weight:{strategy.Weight}");
                return;
            }
            case "play":
            {
                if (_store.Find(arg) is null)
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.not_found", arg]}");
                    return;
                }

                _forcedStrategy = arg;
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.forced", arg]}");
                return;
            }
            case "random":
            {
                _forcedStrategy = null;
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.random"]}");
                return;
            }
            default:
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.usage"]}");
                return;
        }
    }

    private bool TryGetEdited(CCSPlayerController player, CommandInfo info, out ExecuteStrategy? strategy)
    {
        strategy = null;
        if (!_editing.TryGetValue(player.SteamID, out var name) || _store.Find(name) is null)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.not_editing"]}");
            return false;
        }

        strategy = _store.Find(name);
        return strategy is not null;
    }

    private static List<CCSPlayerController> GetAliveTeam(CsTeam team) =>
        Utilities.GetPlayers()
            .Where(p => PlayerHelper.IsValid(p) && p.Team == team && p.PawnIsAlive)
            .ToList();
}
