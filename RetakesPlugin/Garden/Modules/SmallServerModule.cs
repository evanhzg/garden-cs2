using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRetakes.Core.Admin;
using GardenRetakes.Core.GameModes;
using RetakesPlugin.Managers;
using RetakesPlugin.Models;
using RetakesPlugin.Utils;
using RetakesAllocatorCore;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// Small-server overlay (ROADMAP R5) — makes retakes with 2–3 humans enjoyable.
///
/// While active (auto at 1..MaxHumans humans, or forced via css_smallserver):
///  - closer spawns: only spawns flagged "smallserver" are used, when enough
///    exist for the round (place them with !gspawn add ... smallserver);
///  - reduced utility: team nade pool hard-capped (SmallServer.MaxTeamNades);
///  - last CT dies → instant round switch (T win, no waiting for the bomb);
///  - last T dies → instant defuse (global InstantDefuseModule already does it).
/// Activation state is evaluated at every round prestart — before the allocator
/// hands out nades and before spawns are assigned.
/// </summary>
public class SmallServerModule : IGardenModule
{
    public const string SpawnFlag = "smallserver";

    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly AdminModule _admin;
    private bool _wasActive;

    public string Name => "SmallServer";
    public bool Enabled => true;

    public SmallServerModule(RetakesPlugin plugin, GardenHost host, AdminModule admin)
    {
        _plugin = plugin;
        _host = host;
        _admin = admin;
    }

    public bool IsActive => _host.Modes.IsSmallServerActive(CountHumans());

    public void Load(bool hotReload)
    {
        _plugin.AddCommand("css_smallserver", "Small-server overlay: on/off/auto or show state.", OnCommand);

        // Prestart: settle activation BEFORE nade allocation and spawn assignment.
        _plugin.RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart);
        _plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        // R12 reliability: player counts change mid-round too — re-evaluate on
        // every join/leave so activation is never one round late.
        _plugin.RegisterEventHandler<EventPlayerConnectFull>((_, _) =>
        {
            Server.NextFrame(EvaluateActivation);
            return HookResult.Continue;
        });
        _plugin.RegisterEventHandler<EventPlayerDisconnect>((_, _) =>
        {
            Server.NextFrame(EvaluateActivation);
            return HookResult.Continue;
        });

        // Closer spawns: prefer "smallserver"-flagged spawns while active.
        SpawnManager.GardenSpawnFilter = (spawns, _) =>
        {
            if (!IsActive || !_host.Settings.SmallServer.UseFlaggedSpawns)
            {
                return spawns;
            }

            var flagged = spawns
                .Where(s => s.Flags.Contains(SpawnFlag, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return flagged.Count > 0 ? flagged : spawns;
        };
    }

    public void OnMapStart(string mapName)
    {
        _wasActive = false;
        NadeHelpers.GardenMaxTotalNadesOverride = null;
    }

    public void Unload()
    {
        SpawnManager.GardenSpawnFilter = null;
        NadeHelpers.GardenMaxTotalNadesOverride = null;
    }

    private static int CountHumans() =>
        Utilities.GetPlayers().Count(p => PlayerHelper.IsValid(p) && !p.IsBot &&
                                          p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist);

    // ---------- activation + effects ----------

    private HookResult OnRoundPrestart(EventRoundPrestart @event, GameEventInfo info)
    {
        EvaluateActivation();
        return HookResult.Continue;
    }

    private void EvaluateActivation()
    {
        var active = IsActive;

        // Utility cap is read by the allocator at allocation time; keep it in sync
        // every round in case the config changed via !gconfig.
        NadeHelpers.GardenMaxTotalNadesOverride =
            active ? _host.Settings.SmallServer.MaxTeamNades : null;

        if (active == _wasActive)
        {
            return;
        }

        _wasActive = active;
        Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} " +
            $"{_plugin.Localizer[active ? "garden.smallserver.on" : "garden.smallserver.off"]}");
        Logger.LogInfo("Garden/SmallServer", $"Overlay {(active ? "activated" : "deactivated")}");
    }

    // ---------- instant round switch (last CT death) ----------

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        // R12 reliability: compute the overlay state LIVE — the cached prestart
        // flag was one round stale when players joined/left mid-round, which
        // made the instant switch fire only sometimes.
        if (@event.Userid == null || !IsActive ||
            !_host.Settings.SmallServer.InstantRoundSwitchOnLastCtDeath)
        {
            return HookResult.Continue;
        }

        Server.NextFrame(() =>
        {
            if (!IsActive || (GameRulesHelper.GetGameRulesOrNull()?.WarmupPeriod ?? false))
            {
                return;
            }

            var aliveCts = CountAlive(CsTeam.CounterTerrorist);
            var aliveTs = CountAlive(CsTeam.Terrorist);
            if (aliveCts > 0 || aliveTs == 0)
            {
                return;
            }

            Logger.LogInfo("Garden/SmallServer", "Last CT dead — instant round switch.");
            Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} " +
                $"{_plugin.Localizer["garden.smallserver.ct_wiped"]}");
            GameRulesHelper.TerminateRound(RoundEndReason.TerroristsWin);
        });

        return HookResult.Continue;
    }

    // R12: exclude bots — a bot holding the site must not block the instant switch.
    private static int CountAlive(CsTeam team) =>
        Utilities.GetPlayers().Count(p => PlayerHelper.IsValid(p) && !p.IsBot &&
                                          p.Team == team && p.PawnIsAlive);

    // ---------- command ----------

    private void OnCommand(CCSPlayerController? player, CommandInfo info)
    {
        var prefix = _plugin.Localizer["garden.prefix"];
        var arg = info.GetArg(1).ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(arg))
        {
            var state = _host.Modes.SmallServer switch
            {
                SmallServerState.ForcedOn => "on",
                SmallServerState.ForcedOff => "off",
                _ => "auto",
            };
            info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.smallserver.state", state, IsActive ? "✔" : "✘"]}");
            return;
        }

        // Changing the overlay needs at least Moderator.
        if (!_admin.Require(player, info, AdminLevel.Moderator))
        {
            return;
        }

        switch (arg)
        {
            case "on":
                _host.Modes.SetSmallServerState(SmallServerState.ForcedOn);
                break;
            case "off":
                _host.Modes.SetSmallServerState(SmallServerState.ForcedOff);
                break;
            case "auto":
                _host.Modes.SetSmallServerState(SmallServerState.Auto);
                break;
            default:
                info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.smallserver.usage"]}");
                return;
        }

        EvaluateActivation();
        info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.smallserver.state", arg, IsActive ? "✔" : "✘"]}");
    }
}
