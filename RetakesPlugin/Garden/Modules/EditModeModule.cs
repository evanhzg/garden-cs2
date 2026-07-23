using System.Drawing;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRetakes.Core.Admin;
using GardenRetakes.Core.GameModes;
using RetakesPlugin.Models;
using RetakesPlugin.Services;
using RetakesPlugin.Utils;
using RetakesPluginShared.Enums;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// Edit mode (ROADMAP R11): one dedicated mode to edit retakes spawns, duel
/// arenas and execute strategies — no bomb, no timer, noclip, and per-category
/// visual markers so a duel arena never looks like a retakes spawn.
///
///   !gamemode edit          enter/leave (Admin) — retakes machinery is gated
///   !gedit                  open/close the editor menu (Admin, edit mode only)
///
/// The menu works WHILE MOVING (nobody is frozen):
///   R = move the cursor · E = activate/cycle · TAB = close
/// Multi-word names: actions that create something prompt for
///   !name <anything with spaces>          (arenas)
///   !name <a|b> <anything with spaces>    (strategies)
/// </summary>
public class EditModeModule : IGardenModule
{
    private enum Category
    {
        Retakes,
        Duels,
        Executes,
    }

    private enum Prompt
    {
        None,
        NewArena,
        NewStrategy,
        NewScenario,
    }

    private sealed class EditorState
    {
        public Category Category = Category.Retakes;
        public int Index;
        public ulong PreviousButtons;
        public Prompt Prompt = Prompt.None;

        // Retakes options
        public CsTeam Team = CsTeam.Terrorist;
        public Bombsite Site = Bombsite.A;

        // Selection cursors
        public int ArenaIndex = -1;
        public int StrategyIndex = -1;
        public int NadeIndex;

        public string ActiveScenario = "";
        public bool Noclip;

        // Render throttle: only re-send the center-HTML menu when it changes or on
        // a heartbeat — sending it every tick floods the client (choke/rollbacks).
        public string LastMenu = "";
        public long LastRenderTick = long.MinValue;
    }

    private const long TabButton = 8589934592;

    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly AdminModule _admin;
    private readonly DuelsModule _duels;
    private readonly ExecutesModule _executes;

    private readonly Dictionary<ulong, EditorState> _editors = new();
    private readonly List<uint> _markerIndexes = [];
    private bool _tickRegistered;
    private long _tick;

    // Re-send the menu at most this often when nothing changed (~6/s at 64 tick)
    // instead of every tick — keeps it visible without flooding the client.
    private const long RenderHeartbeatTicks = 10;

    public string Name => "EditMode";
    public bool Enabled => true;

    public EditModeModule(RetakesPlugin plugin, GardenHost host, AdminModule admin,
        DuelsModule duels, ExecutesModule executes)
    {
        _plugin = plugin;
        _host = host;
        _admin = admin;
        _duels = duels;
        _executes = executes;
    }

    private bool IsActive => _host.Modes.CurrentMode == GameModeKind.Edit;

    private string Prefix => _plugin.Localizer["garden.prefix"];

    public void Load(bool hotReload)
    {
        _host.Modes.ModeChanged += OnModeChanged;
        _plugin.AddCommand("css_gedit", "Open/close the Garden editor menu (edit mode).", OnEditCommand);
        _plugin.AddCommand("css_name", "Answer a pending editor name prompt.", OnNameCommand);
        _plugin.AddCommand("css_ghost", "Enter admin freecam (Spectator).", OnGhostCommand);
        _plugin.AddCommand("css_freecam", "Enter admin freecam (Spectator).", OnGhostCommand);
        _plugin.AddCommandListener("drop", OnDropCommand);
    }

    public void OnMapStart(string mapName)
    {
        _editors.Clear();
        _markerIndexes.Clear();
    }

    public void Unload()
    {
        _host.Modes.ModeChanged -= OnModeChanged;
    }

    // ---------- mode lifecycle ----------

    private void OnModeChanged(GameModeKind from, GameModeKind to)
    {
        if (to == GameModeKind.Edit)
        {
            StartEdit();
        }
        else if (from == GameModeKind.Edit)
        {
            StopEdit();
        }
    }

    private void StartEdit()
    {
        // No timer, no round flow, no bomb.
        Server.ExecuteCommand("mp_warmup_pausetimer 1");
        Server.ExecuteCommand("mp_warmuptime 999999");
        Server.ExecuteCommand("mp_death_drop_c4 0");
        Server.ExecuteCommand("sv_cheats 1");
        Server.ExecuteCommand("mp_warmup_start");

        _executes.CaptureAllThrows = true;
        EnsureTick();
        _plugin.AddTimer(3.0f, SweepBombs, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT | CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);

        RenderMarkers(new EditorState { Category = Category.Retakes });
        Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.edit.started"]}");
    }

    private void StopEdit()
    {
        _executes.CaptureAllThrows = false;
        foreach (var (steamId, state) in _editors.ToList())
        {
            var player = FindBySteamId(steamId);
            if (player is not null)
            {
                SetNoclip(player, false);
                player.PrintToCenterHtml(" ");
            }
        }

        _editors.Clear();
        ClearMarkers();
        SpawnService.ClearAllSpawnModels();

        Server.ExecuteCommand("mp_death_drop_c4 1");
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("sv_cheats 0");
        Server.ExecuteCommand("mp_warmup_end");
        Server.ExecuteCommand("mp_restartgame 1");
        Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.edit.stopped"]}");
    }

    private void SweepBombs()
    {
        if (!IsActive)
        {
            return;
        }

        foreach (var player in Utilities.GetPlayers().Where(p => PlayerHelper.IsValid(p) && p.PawnIsAlive))
        {
            RetakesAllocator.Helpers.RemoveWeapons(player, i => i == CounterStrikeSharp.API.Modules.Entities.Constants.CsItem.C4);
        }

        foreach (var c4 in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>("weapon_c4"))
        {
            if (c4.IsValid)
            {
                c4.AddEntityIOEvent("Kill", c4, null, "", 0.1f);
            }
        }
    }

    // ---------- commands ----------

    private void OnEditCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!_admin.Require(player, info, AdminLevel.Admin) || !PlayerHelper.IsValid(player))
        {
            return;
        }

        if (!IsActive)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.edit.not_active"]}");
            return;
        }

        var steamId = player!.SteamID;
        if (_editors.Remove(steamId))
        {
            player.PrintToCenterHtml(" ");
            return;
        }

        _editors[steamId] = new EditorState();
        EnsureTick();
        info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.edit.menu_hint"]}");
    }

    private void OnNameCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!PlayerHelper.IsValid(player) || !_editors.TryGetValue(player!.SteamID, out var state) ||
            state.Prompt == Prompt.None)
        {
            return;
        }

        var raw = info.ArgString.Trim();
        switch (state.Prompt)
        {
            case Prompt.NewArena:
            {
                state.Prompt = Prompt.None;
                if (!_duels.ArenaStore.TryAdd(raw, player.PlayerName, out var error))
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.duels.arena_error", error ?? "?"]}");
                    return;
                }

                _duels.SaveArenas();
                state.ArenaIndex = _duels.ArenaStore.Arenas.Count - 1;
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.edit.arena_created", raw]}");
                RenderMarkers(state);
                return;
            }
            case Prompt.NewStrategy:
            {
                var parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2 || parts[0].ToUpperInvariant() is not ("A" or "B"))
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.edit.strategy_name_usage"]}");
                    return;
                }

                state.Prompt = Prompt.None;
                if (!_executes.Store.TryAdd(parts[1], parts[0], player.PlayerName, out var error))
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.exec.error", error ?? "?"]}");
                    return;
                }

                _executes.Save();
                state.StrategyIndex = _executes.Store.Strategies.Count - 1;
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.edit.strategy_created",
                    parts[1], parts[0].ToUpperInvariant()]}");
                RenderMarkers(state);
                return;
            }
            case Prompt.NewScenario:
            {
                state.Prompt = Prompt.None;
                var safeName = raw.Replace(" ", "_");
                state.ActiveScenario = $"scenario:{safeName}";
                info.ReplyToCommand($"{Prefix} Active scenario set to: {safeName}");
                return;
            }
        }
    }

    private void OnGhostCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!PlayerHelper.IsValid(player) || !PlayerHelper.HasAdminPermission(player, "@css/generic")) return;

        player.ChangeTeam(CsTeam.Spectator);
        player.PrintToChat($"{Prefix} You are now in Ghost mode (Spectator freecam). You can still place spawns using the editor!");
    }

    private CBaseEntity? GetValidPawn(CCSPlayerController player)
    {
        var pawn = player.Pawn.Value;
        if (pawn != null && pawn.IsValid && pawn.AbsOrigin != null && pawn.AbsRotation != null)
        {
            return pawn;
        }
        return null;
    }

    private HookResult OnDropCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not null && _editors.TryGetValue(player.SteamID, out var state))
        {
            player.ExecuteClientCommand("play sounds/ui/csgo_ui_contract_type4.vsnd_c");
            DeleteNearestContext(player, state);
            return HookResult.Stop; // Prevent actually dropping
        }
        return HookResult.Continue;
    }

    // ---------- tick: input + render ----------

    private void EnsureTick()
    {
        if (_tickRegistered)
        {
            return;
        }

        _tickRegistered = true;
        _plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    private void OnTick()
    {
        if (!IsActive || _editors.Count == 0)
        {
            return;
        }

        _tick++;

        foreach (var player in Utilities.GetPlayers())
        {
            if (!PlayerHelper.IsValid(player) || player.IsBot || player.IsHLTV)
            {
                continue;
            }

            if (!_editors.TryGetValue(player.SteamID, out var state))
            {
                continue;
            }

            HandleInput(player, state);
            // HandleInput may close the menu (TAB) — don't re-draw it if so.
            if (!_editors.ContainsKey(player.SteamID))
            {
                continue;
            }
            RenderMenu(player, state);
        }
    }

    /// <summary>Rising-edge detection on R / E / TAB while the player moves freely.</summary>
    private void HandleInput(CCSPlayerController player, EditorState state)
    {
        var buttons = (ulong) player.Buttons;
        var pressed = buttons & ~state.PreviousButtons;
        state.PreviousButtons = buttons;

        var options = BuildOptions(state);
        if (options.Count == 0)
        {
            return;
        }

        if ((pressed & (ulong) PlayerButtons.Reload) != 0)
        {
            state.Index = (state.Index + 1) % options.Count;
            player.ExecuteClientCommand("play sounds/ui/csgo_ui_contract_type4.vsnd_c");
        }
        else if ((pressed & (ulong) PlayerButtons.Use) != 0)
        {
            player.ExecuteClientCommand("play sounds/ui/item_sticker_select.vsnd_c");
            options[Math.Min(state.Index, options.Count - 1)].Action(player, state);
        }
        else if ((pressed & TabButton) != 0)
        {
            _editors.Remove(player.SteamID);
            player.PrintToCenterHtml(" ");
        }
        else if ((pressed & (ulong) PlayerButtons.Attack) != 0)
        {
            if (state.Category == Category.Retakes)
            {
                player.ExecuteClientCommand("play sounds/ui/item_sticker_select.vsnd_c");
                AddRetakesSpawn(player, state);
            }
        }
        else if ((pressed & (ulong) PlayerButtons.Attack2) != 0)
        {
            if (state.Category == Category.Retakes)
            {
                player.ExecuteClientCommand("play sounds/ui/csgo_ui_contract_type4.vsnd_c");
                DeleteNearestSpawn(player, state);
            }
        }
        else if ((pressed & (ulong) PlayerButtons.Duck) != 0)
        {
            player.ExecuteClientCommand("play sounds/ui/item_sticker_select.vsnd_c");
            CycleContext(player, state);
        }
    }

    private sealed record MenuOption(string Label, Action<CCSPlayerController, EditorState> Action);

    private List<MenuOption> BuildOptions(EditorState state)
    {
        var options = new List<MenuOption>
        {
            new($"Category: {state.Category}", (p, s) =>
            {
                s.Category = (Category) (((int) s.Category + 1) % 3);
                s.Index = 0;
                RenderMarkers(s);
            }),
        };

        switch (state.Category)
        {
            case Category.Retakes:
            {
                options.Add(new($"Team: {(state.Team == CsTeam.Terrorist ? "T" : "CT")}",
                    (p, s) => s.Team = s.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist));
                options.Add(new($"Site: {state.Site}",
                    (p, s) => {
                        var sites = Enum.GetValues<Bombsite>();
                        var idx = Array.IndexOf(sites, s.Site);
                        s.Site = sites[(idx + 1) % sites.Length];
                        RenderMarkers(s);
                    }));

                var allScenarios = _plugin.MapConfigService?.GetSpawnsClone()
                    .SelectMany(sp => sp.Flags)
                    .Where(f => f.StartsWith("scenario:", StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .ToList() ?? [];
                
                options.Add(new($"Scenario: {(string.IsNullOrEmpty(state.ActiveScenario) ? "None" : state.ActiveScenario.Replace("scenario:", ""))}", (p, s) =>
                {
                    if (allScenarios.Count == 0) return;
                    var idx = string.IsNullOrEmpty(s.ActiveScenario) ? -1 : allScenarios.IndexOf(s.ActiveScenario);
                    idx = (idx + 1) % (allScenarios.Count + 1);
                    s.ActiveScenario = idx == allScenarios.Count ? "" : allScenarios[idx];
                }));
                options.Add(new("🆕 New scenario (then: !name <name>)", (p, s) =>
                {
                    s.Prompt = Prompt.NewScenario;
                    p.PrintToChat($"{Prefix} Type !name <scenario_name> in chat to create a new scenario.");
                }));
                
                if (!string.IsNullOrEmpty(state.ActiveScenario))
                {
                    options.Add(new($"🏷️ Toggle '{state.ActiveScenario.Replace("scenario:", "")}' on nearest", ToggleScenarioNearest));
                }

                options.Add(new("➕ Add spawn here", AddRetakesSpawn));
                options.Add(new("🏴 Toggle planter on nearest", TogglePlanterNearest));
                options.Add(new("🗑 Delete nearest spawn", DeleteNearestSpawn));
                break;
            }

            case Category.Duels:
            {
                var arenas = _duels.ArenaStore.Arenas;
                var arenaLabel = state.ArenaIndex >= 0 && state.ArenaIndex < arenas.Count
                    ? arenas[state.ArenaIndex].Name
                    : "—";
                options.Add(new($"Arena: {arenaLabel}", (p, s) =>
                {
                    if (arenas.Count == 0) return;
                    s.ArenaIndex = (s.ArenaIndex + 1) % arenas.Count;
                }));
                options.Add(new("🆕 New arena (then: !name <name>)", (p, s) =>
                {
                    s.Prompt = Prompt.NewArena;
                    p.PrintToChat($"{Prefix} {_plugin.Localizer["garden.edit.arena_name_usage"]}");
                }));
                options.Add(new("➕ Add Spawn A here", (p, s) => AddArenaSpawn(p, s, isA: true)));
                options.Add(new("➕ Add Spawn B here", (p, s) => AddArenaSpawn(p, s, isA: false)));
                options.Add(new("🗑 Delete selected arena", DeleteSelectedArena));
                break;
            }

            case Category.Executes:
            {
                var strategies = _executes.Store.Strategies;
                var strategyLabel = state.StrategyIndex >= 0 && state.StrategyIndex < strategies.Count
                    ? strategies[state.StrategyIndex].Name
                    : "—";
                options.Add(new($"Strategy: {strategyLabel}", (p, s) =>
                {
                    if (strategies.Count == 0) return;
                    s.StrategyIndex = (s.StrategyIndex + 1) % strategies.Count;
                    s.NadeIndex = 0;
                    RenderMarkers(s);
                }));
                options.Add(new("🆕 New strategy (then: !name <a|b> <name>)", (p, s) =>
                {
                    s.Prompt = Prompt.NewStrategy;
                    p.PrintToChat($"{Prefix} {_plugin.Localizer["garden.edit.strategy_name_usage"]}");
                }));
                options.Add(new("➕ Add T start here", (p, s) => AddStrategyPosition(p, s, isT: true)));
                options.Add(new("➕ Add CT setup here", (p, s) => AddStrategyPosition(p, s, isT: false)));
                options.Add(new("💣 Save my last thrown nade", SaveLastNade));

                // Nade management for the selected strategy (R12: preview + delete).
                var selStrategy = state.StrategyIndex >= 0 && state.StrategyIndex < strategies.Count
                    ? strategies[state.StrategyIndex]
                    : null;
                if (selStrategy is not null && selStrategy.Utilities.Count > 0)
                {
                    var count = selStrategy.Utilities.Count;
                    var ni = Math.Clamp(state.NadeIndex, 0, count - 1);
                    var u = selStrategy.Utilities[ni];
                    options.Add(new($"Nade {ni + 1}/{count}: {u.Team} {u.Type} +{u.DelaySeconds:0.0}s",
                        (p, s) => s.NadeIndex = (s.NadeIndex + 1) % count));
                    options.Add(new("👁 Preview selected nade", PreviewNade));
                    options.Add(new("🗑 Delete selected nade", DeleteNade));
                }

                options.Add(new("🗑 Delete selected strategy", DeleteSelectedStrategy));
                break;
            }
        }

        options.Add(new(state.Noclip ? "🚀 Noclip: ON" : "🚀 Noclip: OFF", (p, s) =>
        {
            s.Noclip = !s.Noclip;
            SetNoclip(p, s.Noclip);
        }));
        options.Add(new("❌ Close menu", (p, s) =>
        {
            _editors.Remove(p.SteamID);
            p.PrintToCenterHtml(" ");
        }));

        return options;
    }

    private void RenderMenu(CCSPlayerController player, EditorState state)
    {
        var options = BuildOptions(state);
        if (state.Index >= options.Count)
        {
            state.Index = 0;
        }

        // Center HTML can only show a handful of lines — render a scrolling
        // window around the cursor so long menus (e.g. Executes) are all reachable.
        const int window = 6;
        var start = Math.Clamp(state.Index - window / 2, 0, Math.Max(0, options.Count - window));
        var end = Math.Min(options.Count, start + window);

        var builder = new StringBuilder();
        builder.AppendLine($"<font color='#d946ef'>█░ GARDEN EDITOR — {state.Category} ░█</font><br>");
        if (start > 0)
        {
            builder.AppendLine($"<font color='gray'>▲ {start} more ▲</font><br>");
        }
        for (var i = start; i < end; i++)
        {
            builder.AppendLine(i == state.Index
                ? $"<font color='orange'>▶ {options[i].Label} ◀</font><br>"
                : $"<font color='white'>{options[i].Label}</font><br>");
        }
        if (end < options.Count)
        {
            builder.AppendLine($"<font color='gray'>▼ {options.Count - end} more ▼</font><br>");
        }

        builder.AppendLine("<font color='cyan'>[ R - Cycle ]</font> <font color='lime'>[ E - Select ]</font> " +
                           "<font color='orange'>[ TAB - Close ]</font>");

        // Throttle: only push over the network when the menu actually changed or a
        // heartbeat is due. Sending every tick was choking clients (rollbacks/TPs).
        var menu = builder.ToString();
        if (menu == state.LastMenu && _tick - state.LastRenderTick < RenderHeartbeatTicks)
        {
            return;
        }
        state.LastMenu = menu;
        state.LastRenderTick = _tick;
        player.PrintToCenterHtml(menu);
    }

    // ---------- actions ----------

    private void AddRetakesSpawn(CCSPlayerController player, EditorState state)
    {
        var pawn = GetValidPawn(player);
        if (pawn == null || _plugin.MapConfigService is null)
        {
            return;
        }

        var spawn = new Spawn(
            new Vector(pawn.AbsOrigin!.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z),
            new QAngle(pawn.AbsRotation!.X, pawn.AbsRotation.Y, pawn.AbsRotation.Z))
        {
            Team = state.Team,
            Bombsite = state.Site,
            CanBePlanter = state.Team == CsTeam.Terrorist && (pawn as CCSPlayerPawn)?.InBombZoneTrigger == true,
            AddedBy = player.PlayerName,
        };

        if (_plugin.MapConfigService.AddSpawn(spawn))
        {
            _plugin.SpawnManager?.CalculateMapSpawns();
            player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.spawn.added",
                player.PlayerName, state.Team == CsTeam.Terrorist ? "T" : "CT", state.Site.ToString(), ""]}");
            RenderMarkers(state);
        }
    }

    private void TogglePlanterNearest(CCSPlayerController player, EditorState state)
    {
        var nearest = FindNearestSpawn(player);
        if (nearest is null || _plugin.MapConfigService is null)
        {
            return;
        }

        _plugin.MapConfigService.MutateSpawns(_ => nearest.CanBePlanter = !nearest.CanBePlanter);
        _plugin.SpawnManager?.CalculateMapSpawns();
        player.PrintToChat($"{Prefix} {_plugin.Localizer[
            nearest.CanBePlanter ? "garden.spawn.flag_on" : "garden.spawn.flag_off",
            "planter", nearest.Team == CsTeam.Terrorist ? "T" : "CT", nearest.Bombsite.ToString()]}");
        RenderMarkers(state);
    }

    private void ToggleScenarioNearest(CCSPlayerController player, EditorState state)
    {
        var nearest = FindNearestSpawn(player);
        if (nearest is null || _plugin.MapConfigService is null || string.IsNullOrEmpty(state.ActiveScenario))
        {
            return;
        }

        var hasScenario = nearest.Flags.Contains(state.ActiveScenario, StringComparer.OrdinalIgnoreCase);
        _plugin.MapConfigService.MutateSpawns(_ => 
        {
            if (hasScenario) nearest.Flags.RemoveAll(f => f.Equals(state.ActiveScenario, StringComparison.OrdinalIgnoreCase));
            else nearest.Flags.Add(state.ActiveScenario);
        });
        _plugin.SpawnManager?.CalculateMapSpawns();

        player.PrintToChat($"{Prefix} {(hasScenario ? "Removed" : "Added")} '{state.ActiveScenario.Replace("scenario:", "")}' {(hasScenario ? "from" : "to")} nearest spawn.");
        RenderMarkers(state);
    }

    private void DeleteNearestSpawn(CCSPlayerController player, EditorState state)
    {
        var nearest = FindNearestSpawn(player);
        if (nearest is null || _plugin.MapConfigService is null)
        {
            return;
        }

        if (_plugin.MapConfigService.RemoveSpawn(nearest))
        {
            _plugin.SpawnManager?.CalculateMapSpawns();
            player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.spawn.deleted",
                player.PlayerName, nearest.Team == CsTeam.Terrorist ? "T" : "CT", nearest.Bombsite.ToString()]}");
            RenderMarkers(state);
        }
    }

    private void CycleContext(CCSPlayerController player, EditorState state)
    {
        switch (state.Category)
        {
            case Category.Retakes:
                var sites = Enum.GetValues<Bombsite>();
                var idx = Array.IndexOf(sites, state.Site);
                state.Site = sites[(idx + 1) % sites.Length];
                RenderMarkers(state);
                player.PrintToChat($"{Prefix} Switched site to {state.Site}");
                break;
            case Category.Duels:
                var arenas = _duels.ArenaStore.Arenas;
                if (arenas.Count > 0)
                {
                    state.ArenaIndex = (state.ArenaIndex + 1) % arenas.Count;
                    RenderMarkers(state);
                    player.PrintToChat($"{Prefix} Switched arena to {arenas[state.ArenaIndex].Name}");
                }
                break;
            case Category.Executes:
                var strategies = _executes.Store.Strategies;
                if (strategies.Count > 0)
                {
                    state.StrategyIndex = (state.StrategyIndex + 1) % strategies.Count;
                    state.NadeIndex = 0;
                    RenderMarkers(state);
                    player.PrintToChat($"{Prefix} Switched strategy to {strategies[state.StrategyIndex].Name}");
                }
                break;
        }
    }

    private void DeleteNearestContext(CCSPlayerController player, EditorState state)
    {
        switch (state.Category)
        {
            case Category.Retakes:
                DeleteNearestSpawn(player, state);
                break;
            case Category.Duels:
                DeleteNearestArenaSpawn(player, state);
                break;
            case Category.Executes:
                DeleteNearestExecutePosition(player, state);
                break;
        }
    }

    private void DeleteNearestArenaSpawn(CCSPlayerController player, EditorState state)
    {
        var arenas = _duels.ArenaStore.Arenas;
        if (state.ArenaIndex < 0 || state.ArenaIndex >= arenas.Count) return;
        var arena = arenas[state.ArenaIndex];
        
        var pawn = GetValidPawn(player);
        if (pawn is null) return;
        var origin = pawn.AbsOrigin!;

        double bestDist = 300.0;
        ExecutePosition? nearest = null;
        bool isA = true;

        foreach (var pos in arena.SpawnsA)
        {
            var d = GameRulesHelper.GetDistanceBetweenVectors(new Vector(pos.X, pos.Y, pos.Z), origin);
            if (d < bestDist) { bestDist = d; nearest = pos; isA = true; }
        }
        foreach (var pos in arena.SpawnsB)
        {
            var d = GameRulesHelper.GetDistanceBetweenVectors(new Vector(pos.X, pos.Y, pos.Z), origin);
            if (d < bestDist) { bestDist = d; nearest = pos; isA = false; }
        }

        // Check legacy as well just in case they aren't fully migrated yet
        if (arena.EndA is not null)
        {
            var d = GameRulesHelper.GetDistanceBetweenVectors(new Vector(arena.EndA.X, arena.EndA.Y, arena.EndA.Z), origin);
            if (d < bestDist) { bestDist = d; nearest = arena.EndA; isA = true; }
        }
        if (arena.EndB is not null)
        {
            var d = GameRulesHelper.GetDistanceBetweenVectors(new Vector(arena.EndB.X, arena.EndB.Y, arena.EndB.Z), origin);
            if (d < bestDist) { bestDist = d; nearest = arena.EndB; isA = false; }
        }

        if (nearest is not null)
        {
            if (nearest == arena.EndA) arena.EndA = null;
            else if (nearest == arena.EndB) arena.EndB = null;
            else if (isA) arena.SpawnsA.Remove(nearest);
            else arena.SpawnsB.Remove(nearest);
            
            _duels.SaveArenas();
            player.PrintToChat($"{Prefix} Deleted spawn {(isA ? "A" : "B")} from {arena.Name}.");
            RenderMarkers(state);
        }
        else
        {
            player.PrintToChat($"{Prefix} No spawn close enough to delete.");
        }
    }

    private void DeleteNearestExecutePosition(CCSPlayerController player, EditorState state)
    {
        var strats = _executes.Store.Strategies;
        if (state.StrategyIndex < 0 || state.StrategyIndex >= strats.Count) return;
        var strat = strats[state.StrategyIndex];
        
        var pawn = GetValidPawn(player);
        if (pawn is null) return;
        var origin = pawn.AbsOrigin!;

        double bestDist = 300.0;
        ExecutePosition? nearest = null;
        bool isT = true;

        foreach (var pos in strat.TStarts)
        {
            var d = GameRulesHelper.GetDistanceBetweenVectors(new Vector(pos.X, pos.Y, pos.Z), origin);
            if (d < bestDist) { bestDist = d; nearest = pos; isT = true; }
        }
        foreach (var pos in strat.CtSetups)
        {
            var d = GameRulesHelper.GetDistanceBetweenVectors(new Vector(pos.X, pos.Y, pos.Z), origin);
            if (d < bestDist) { bestDist = d; nearest = pos; isT = false; }
        }

        if (nearest is not null)
        {
            if (isT) strat.TStarts.Remove(nearest);
            else strat.CtSetups.Remove(nearest);
            _executes.Save();
            player.PrintToChat($"{Prefix} Deleted {(isT ? "T start" : "CT setup")} from {strat.Name}.");
            RenderMarkers(state);
        }
        else
        {
            player.PrintToChat($"{Prefix} No position close enough to delete.");
        }
    }

    private Spawn? FindNearestSpawn(CCSPlayerController player)
    {
        var pawn = GetValidPawn(player);
        if (pawn is null || _plugin.MapConfigService is null)
        {
            return null;
        }
        var origin = pawn.AbsOrigin!;

        Spawn? nearest = null;
        var best = 300.0;
        foreach (var spawn in _plugin.MapConfigService.GetSpawnsClone())
        {
            var d = GameRulesHelper.GetDistanceBetweenVectors(spawn.Vector, origin);
            if (d < best)
            {
                best = d;
                nearest = spawn;
            }
        }

        return nearest;
    }

    private void AddArenaSpawn(CCSPlayerController player, EditorState state, bool isA)
    {
        var arenas = _duels.ArenaStore.Arenas;
        var pawn = GetValidPawn(player);
        if (state.ArenaIndex < 0 || state.ArenaIndex >= arenas.Count || pawn == null)
        {
            player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.edit.no_selection"]}");
            return;
        }

        var position = new ExecutePosition
        {
            X = pawn.AbsOrigin!.X, Y = pawn.AbsOrigin.Y, Z = pawn.AbsOrigin.Z,
            Pitch = pawn.AbsRotation!.X, Yaw = pawn.AbsRotation.Y,
        };

        var arena = arenas[state.ArenaIndex];
        if (isA) arena.SpawnsA.Add(position);
        else arena.SpawnsB.Add(position);
        _duels.SaveArenas();

        player.PrintToChat($"{Prefix} Added spawn to {arena.Name} End {(isA ? "A" : "B")}.");
        RenderMarkers(state);
    }

    private void DeleteSelectedArena(CCSPlayerController player, EditorState state)
    {
        var arenas = _duels.ArenaStore.Arenas;
        if (state.ArenaIndex < 0 || state.ArenaIndex >= arenas.Count)
        {
            return;
        }

        var name = arenas[state.ArenaIndex].Name;
        _duels.ArenaStore.Remove(name);
        _duels.SaveArenas();
        state.ArenaIndex = -1;
        player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.duels.arena_deleted", name]}");
        RenderMarkers(state);
    }

    private void AddStrategyPosition(CCSPlayerController player, EditorState state, bool isT)
    {
        var strategies = _executes.Store.Strategies;
        if (state.StrategyIndex < 0 || state.StrategyIndex >= strategies.Count ||
            !PlayerHelper.HasAlivePawn(player))
        {
            player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.edit.no_selection"]}");
            return;
        }

        var pawn = player.PlayerPawn.Value!;
        var strategy = strategies[state.StrategyIndex];
        (isT ? strategy.TStarts : strategy.CtSetups).Add(new ExecutePosition
        {
            X = pawn.AbsOrigin!.X, Y = pawn.AbsOrigin.Y, Z = pawn.AbsOrigin.Z,
            Pitch = pawn.AbsRotation!.X, Yaw = pawn.AbsRotation.Y,
        });
        _executes.Save();

        player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.exec.position_added",
            isT ? "T" : "CT", strategy.Name, strategy.TStarts.Count, strategy.CtSetups.Count]}");
        RenderMarkers(state);
    }

    private void SaveLastNade(CCSPlayerController player, EditorState state)
    {
        var strategies = _executes.Store.Strategies;
        if (state.StrategyIndex < 0 || state.StrategyIndex >= strategies.Count)
        {
            player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.edit.no_selection"]}");
            return;
        }

        if (!_executes.TryGetLastThrow(player.SteamID, out var utility))
        {
            player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.exec.no_throw"]}");
            return;
        }

        var strategy = strategies[state.StrategyIndex];
        strategy.Utilities.Add(utility);
        _executes.Save();
        player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.exec.nade_added",
            $"{utility.Team} {utility.Type}", strategy.Name, utility.DelaySeconds.ToString("0.0"),
            strategy.Utilities.Count]}");
        RenderMarkers(state);
    }

    private bool TryGetSelectedNade(EditorState state, out ExecuteStrategy strategy, out int index)
    {
        strategy = null!;
        index = -1;
        var strategies = _executes.Store.Strategies;
        if (state.StrategyIndex < 0 || state.StrategyIndex >= strategies.Count)
        {
            return false;
        }
        strategy = strategies[state.StrategyIndex];
        if (strategy.Utilities.Count == 0)
        {
            return false;
        }
        index = Math.Clamp(state.NadeIndex, 0, strategy.Utilities.Count - 1);
        return true;
    }

    /// <summary>Throw the selected saved nade so the editor can see its lineup.</summary>
    private void PreviewNade(CCSPlayerController player, EditorState state)
    {
        if (!TryGetSelectedNade(state, out var strategy, out var index))
        {
            player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.edit.no_selection"]}");
            return;
        }
        var utility = strategy.Utilities[index];
        _executes.ThrowUtility(utility);
        player.PrintToChat($"{Prefix} Previewing nade {index + 1} ({utility.Team} {utility.Type}).");
    }

    /// <summary>Remove the selected saved nade from the strategy.</summary>
    private void DeleteNade(CCSPlayerController player, EditorState state)
    {
        if (!TryGetSelectedNade(state, out var strategy, out var index))
        {
            player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.edit.no_selection"]}");
            return;
        }
        var removed = strategy.Utilities[index];
        strategy.Utilities.RemoveAt(index);
        _executes.Save();
        if (state.NadeIndex >= strategy.Utilities.Count)
        {
            state.NadeIndex = Math.Max(0, strategy.Utilities.Count - 1);
        }
        player.PrintToChat($"{Prefix} Deleted {removed.Team} {removed.Type} nade — {strategy.Utilities.Count} left on {strategy.Name}.");
        RenderMarkers(state);
    }

    private void DeleteSelectedStrategy(CCSPlayerController player, EditorState state)
    {
        var strategies = _executes.Store.Strategies;
        if (state.StrategyIndex < 0 || state.StrategyIndex >= strategies.Count)
        {
            return;
        }

        var name = strategies[state.StrategyIndex].Name;
        _executes.Store.Remove(name);
        _executes.Save();
        state.StrategyIndex = -1;
        player.PrintToChat($"{Prefix} {_plugin.Localizer["garden.exec.deleted", name]}");
        RenderMarkers(state);
    }

    // ---------- noclip ----------

    private static void SetNoclip(CCSPlayerController player, bool enabled)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid)
        {
            return;
        }

        pawn.MoveType = enabled ? MoveType_t.MOVETYPE_NOCLIP : MoveType_t.MOVETYPE_WALK;
        Schema.SetSchemaValue(pawn.Handle, "CBaseEntity", "m_nActualMoveType", enabled ? 8 : 2);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    // ---------- markers ----------

    private void RenderMarkers(EditorState state)
    {
        ClearMarkers();
        SpawnService.ClearAllSpawnModels();

        switch (state.Category)
        {
            case Category.Retakes when _plugin.MapConfigService is not null:
                var filteredSpawns = _plugin.MapConfigService.GetSpawnsClone()
                    .Where(s => s.Bombsite == state.Site && 
                                (string.IsNullOrEmpty(state.ActiveScenario) 
                                    ? s.Flags.Count == 0 || s.Flags.All(f => !f.StartsWith("scenario:", StringComparison.OrdinalIgnoreCase))
                                    : s.Flags.Contains(state.ActiveScenario, StringComparer.OrdinalIgnoreCase)))
                    .ToList();
                SpawnService.ShowSpawns(_plugin, filteredSpawns, state.Site);
                break;

            case Category.Duels:
                if (state.ArenaIndex >= 0 && state.ArenaIndex < _duels.ArenaStore.Arenas.Count)
                {
                    var arena = _duels.ArenaStore.Arenas[state.ArenaIndex];
                    foreach (var position in arena.SpawnsA)
                        CreateMarker(position, Color.MediumPurple, $"🏟 {arena.Name}\nSpawn A");
                    foreach (var position in arena.SpawnsB)
                        CreateMarker(position, Color.Magenta, $"🏟 {arena.Name}\nSpawn B");
                    
                    // Legacy markers
                    if (arena.EndA is not null)
                        CreateMarker(arena.EndA, Color.MediumPurple, $"🏟 {arena.Name}\nEnd A");
                    if (arena.EndB is not null)
                        CreateMarker(arena.EndB, Color.Magenta, $"🏟 {arena.Name}\nEnd B");
                }
                break;

            case Category.Executes:
            {
                var strategies = _executes.Store.Strategies;
                if (state.StrategyIndex >= 0 && state.StrategyIndex < strategies.Count)
                {
                    var strategy = strategies[state.StrategyIndex];
                    foreach (var position in strategy.TStarts)
                        CreateMarker(position, Color.Orange, $"⚔ {strategy.Name}\nT start");
                    foreach (var position in strategy.CtSetups)
                        CreateMarker(position, Color.DeepSkyBlue, $"🛡 {strategy.Name}\nCT setup");
                    foreach (var utility in strategy.Utilities)
                        CreateLabel(new Vector(utility.X, utility.Y, utility.Z + 24f), Color.LimeGreen,
                            $"💨 {strategy.Name}\n{utility.Team} {utility.Type} +{utility.DelaySeconds:0.0}s");
                }
                break;
            }
        }
    }

    /// <summary>Agent-model marker + floating label (same technique as SpawnService).</summary>
    private void CreateMarker(ExecutePosition position, Color color, string label)
    {
        try
        {
            var model = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
            if (model is null || !model.IsValid)
            {
                return;
            }

            model.SetModel("agents/models/ctm_sas/ctm_sas.vmdl");
            model.UseAnimGraph = false;
            model.AcceptInput("SetAnimation", value: "tools_preview");
            model.DispatchSpawn();
            model.Render = Color.FromArgb(200, color.R, color.G, color.B);
            if (model.Glow != null)
            {
                model.Glow.GlowColorOverride = color;
                model.Glow.GlowRange = 2000;
                model.Glow.GlowType = 3;
                model.Glow.GlowRangeMin = 25;
            }

            model.Teleport(new Vector(position.X, position.Y, position.Z),
                new QAngle(0, position.Yaw, 0), new Vector(0, 0, 0));
            if (model.Index != 0) _markerIndexes.Add(model.Index);

            CreateLabel(new Vector(position.X, position.Y, position.Z + 80f), color, label);
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/Edit", $"Marker failed: {ex.Message}");
        }
    }

    private void CreateLabel(Vector position, Color color, string text)
    {
        try
        {
            var label = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
            if (label is null)
            {
                return;
            }

            label.MessageText = text;
            label.Enabled = true;
            label.FontSize = 25f;
            label.Color = color;
            label.Fullbright = true;
            label.WorldUnitsPerPx = 0.1f;
            label.DepthOffset = 0.0f;
            label.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
            label.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;
            label.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
            label.Teleport(position, new QAngle(0, 90, 90));
            label.DispatchSpawn();
            if (label.Index != 0) _markerIndexes.Add(label.Index);
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/Edit", $"Label failed: {ex.Message}");
        }
    }

    private void ClearMarkers()
    {
        foreach (var index in _markerIndexes)
        {
            var entity = Utilities.GetEntityFromIndex<CBaseEntity>((int) index);
            if (entity is not null && entity.IsValid)
            {
                entity.Remove();
            }
        }

        _markerIndexes.Clear();
    }

    private static CCSPlayerController? FindBySteamId(ulong steamId) =>
        Utilities.GetPlayers().FirstOrDefault(p =>
            PlayerHelper.IsValid(p) && !p.IsBot && p.SteamID == steamId);
}
