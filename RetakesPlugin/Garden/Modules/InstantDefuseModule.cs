using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesPlugin.Utils;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// Instant defuse (ROADMAP R0): when the last T dies while the bomb is planted
/// and at least one CT is alive, end the round immediately as a defuse instead
/// of making everyone watch the timer.
///
/// With BlockOnUtilityDanger, the defuse is delayed (rechecked every 0.25s)
/// while an HE/molotov projectile is in the air or a fire is burning — the
/// classic "the nade would have killed the defuser" rule.
/// </summary>
public class InstantDefuseModule : IGardenModule
{
    private static readonly string[] DangerDesignerNames =
    [
        "hegrenade_projectile",
        "molotov_projectile",
        "inferno",
    ];

    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private bool _pendingRecheck;

    public string Name => "InstantDefuse";
    public bool Enabled => _host.Settings.InstantDefuse.Enabled;

    public InstantDefuseModule(RetakesPlugin plugin, GardenHost host)
    {
        _plugin = plugin;
        _host = host;
    }

    public void Load(bool hotReload)
    {
        _plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
    }

    public void OnMapStart(string mapName)
    {
        _pendingRecheck = false;
    }

    public void Unload() { }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!Enabled || @event.Userid == null)
        {
            return HookResult.Continue;
        }

        // Entity state (alive counts, C4) is settled on the next frame.
        Server.NextFrame(TryInstantDefuse);
        return HookResult.Continue;
    }

    private void TryInstantDefuse()
    {
        if (!Enabled || !_plugin.RetakesGameplayActive)
        {
            return;
        }

        var gameRules = GameRulesHelper.GetGameRulesOrNull();
        if (gameRules == null || gameRules.WarmupPeriod || !gameRules.BombPlanted || gameRules.BombDefused)
        {
            return;
        }

        var aliveTs = CountAlive(CsTeam.Terrorist);
        var aliveCts = CountAlive(CsTeam.CounterTerrorist);
        if (aliveTs > 0 || aliveCts == 0)
        {
            return;
        }

        if (_host.Settings.InstantDefuse.BlockOnUtilityDanger && IsUtilityDangerPresent())
        {
            // Recheck until the danger clears (round end/explosion cancels naturally).
            if (!_pendingRecheck)
            {
                _pendingRecheck = true;
                _plugin.AddTimer(0.25f, () =>
                {
                    _pendingRecheck = false;
                    TryInstantDefuse();
                });
            }
            return;
        }

        var plantedC4 = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").FirstOrDefault();
        if (plantedC4 == null || !plantedC4.IsValid || !plantedC4.BombTicking)
        {
            return;
        }

        plantedC4.BombTicking = false;
        gameRules.BombDefused = true;

        Logger.LogInfo("Garden/InstantDefuse", "Last T dead — instant defuse.");
        Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.instant_defuse"]}");
        GameRulesHelper.TerminateRound(RoundEndReason.BombDefused);
    }

    private static int CountAlive(CsTeam team) =>
        Utilities.GetPlayers().Count(p => PlayerHelper.IsValid(p) && p.Team == team && p.PawnIsAlive);

    private static bool IsUtilityDangerPresent() =>
        DangerDesignerNames.Any(designerName =>
            Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(designerName).Any(e => e.IsValid));
}
