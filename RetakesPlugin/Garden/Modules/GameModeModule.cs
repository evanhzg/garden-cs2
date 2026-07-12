using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using GardenRetakes.Core.Admin;
using GardenRetakes.Core.GameModes;
using RetakesPlugin.Utils;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// Mode switching front-end (ROADMAP R0). Owns css_gamemode; the actual round
/// flow of Duels/Executes/FastStrat lands in R4/R6/R7 — until then, switching
/// to an unimplemented mode is refused with a "coming soon" message.
/// </summary>
public class GameModeModule : IGardenModule
{
    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly AdminModule _admin;

    public string Name => "GameMode";
    public bool Enabled => true;

    public GameModeModule(RetakesPlugin plugin, GardenHost host, AdminModule admin)
    {
        _plugin = plugin;
        _host = host;
        _admin = admin;
    }

    public void Load(bool hotReload)
    {
        _plugin.AddCommand("css_gamemode", "Show or change the Garden game mode.", OnGameModeCommand);
        _plugin.AddCommand("css_gmode", "Show or change the Garden game mode.", OnGameModeCommand);

        _host.Modes.ModeChanged += (from, to) =>
            Logger.LogInfo("Garden/GameMode", $"Mode changed: {from} -> {to}");
    }

    public void OnMapStart(string mapName)
    {
        // A non-Retakes mode must not survive a map change. The editor especially
        // (GameModeKind.Edit) would otherwise persist and keep RetakesGameplayActive
        // false — so the allocator never runs and players spawn with no weapons.
        // A fresh map always returns to Retakes; admins re-issue !gamemode if needed.
        if (_host.Modes.CurrentMode != GameModeKind.Retakes && !_host.Modes.IsMatchInProgress)
        {
            var previous = _host.Modes.CurrentMode;
            if (_host.Modes.TryChangeMode(GameModeKind.Retakes, out _))
            {
                Logger.LogInfo("Garden/GameMode", $"Map start: reset {previous} -> Retakes (weapons/allocation restored).");
            }
        }
    }

    public void Unload() { }

    // All four modes are implemented (R0/R4/R6/R7).
    private bool IsModeImplemented(GameModeKind mode) => true;

    private void OnGameModeCommand(CCSPlayerController? player, CommandInfo info)
    {
        var prefix = _plugin.Localizer["garden.prefix"];
        var arg = info.GetArg(1);

        if (string.IsNullOrWhiteSpace(arg))
        {
            info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.mode.current", _host.Modes.CurrentMode.ToString()]}");
            return;
        }

        if (!_admin.Require(player, info, AdminLevel.Admin))
        {
            return;
        }

        if (!GameModeManager.TryParseMode(arg, out var target))
        {
            info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.mode.unknown", arg]}");
            return;
        }

        if (!IsModeImplemented(target))
        {
            info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.mode.coming_soon", target.ToString()]}");
            return;
        }

        if (!_host.Modes.TryChangeMode(target, out var error))
        {
            info.ReplyToCommand($"{prefix} {_plugin.Localizer[$"garden.mode.error_{error}"]}");
            return;
        }

        CounterStrikeSharp.API.Server.PrintToChatAll(
            $"{prefix} {_plugin.Localizer["garden.mode.changed", target.ToString()]}");
    }
}
