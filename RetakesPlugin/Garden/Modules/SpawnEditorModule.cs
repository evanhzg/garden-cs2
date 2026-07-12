using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRetakes.Core.Admin;
using RetakesPlugin.Models;
using RetakesPlugin.Services;
using RetakesPlugin.Utils;
using RetakesPluginShared.Enums;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// Garden spawn editor (ROADMAP R1) — layers on top of the base editor
/// (css_showspawns/css_add/...), adding: all-sites rendering, mode flags
/// (duel/smallserver/execute — consumed by R4/R5/R6), attribution, move,
/// per-player teleport testing and dry-run rounds on a chosen site.
///
/// Commands (Admin level via the Garden admin registry):
///   css_gspawns &lt;a|b|all|flag &lt;name&gt;|off&gt;      — render spawns
///   css_gspawn add &lt;t|ct&gt; &lt;a|b&gt; [flags...]     — place at your feet/aim direction
///   css_gspawn del                              — delete nearest
///   css_gspawn move                             — move nearest to your position
///   css_gspawn flag &lt;name&gt;                     — toggle a flag on nearest
///   css_gspawn info                             — nearest spawn details
///   css_gspawn test [a|b|all]                   — teleport through spawns (repeat = next)
///   css_gspawn round &lt;a|b|off&gt;                 — dry-run rounds on one site
/// Multi-editor: all edits go through the single in-memory MapConfigService and
/// are saved immediately, so several admins can edit at once; every edit is
/// announced with the editor's name and stored as AddedBy on the spawn.
/// </summary>
public class SpawnEditorModule : IGardenModule
{
    private const double NearestMaxDistance = 300.0;
    private static readonly string[] KnownFlags = ["duel", "smallserver", "execute"];

    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly AdminModule _admin;

    // Render state (shared view — the last requested one wins, announced to all).
    private string _renderMode = "off"; // off | a | b | all | flag:<name>

    // Per-player teleport-test cursor.
    private readonly Dictionary<ulong, int> _testCursor = new();

    public string Name => "SpawnEditor";
    public bool Enabled => true;

    public SpawnEditorModule(RetakesPlugin plugin, GardenHost host, AdminModule admin)
    {
        _plugin = plugin;
        _host = host;
        _admin = admin;
    }

    public void Load(bool hotReload)
    {
        _plugin.AddCommand("css_gspawns", "Garden spawn editor: render spawns (a|b|all|flag <name>|off).", OnSpawnsCommand);
        _plugin.AddCommand("css_gspawn", "Garden spawn editor: add|del|move|flag|info|test|round.", OnSpawnCommand);
    }

    public void OnMapStart(string mapName)
    {
        _renderMode = "off";
        _testCursor.Clear();
    }

    public void Unload() { }

    private string Prefix => _plugin.Localizer["garden.prefix"];

    // ---------- rendering ----------

    private void OnSpawnsCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!_admin.Require(player, info, AdminLevel.Admin) || !PlayerHelper.IsValid(player))
        {
            return;
        }

        if (_plugin.MapConfigService is null)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.not_ready"]}");
            return;
        }

        var arg = info.GetArg(1).ToLowerInvariant();
        switch (arg)
        {
            case "off":
                _renderMode = "off";
                SpawnService.ClearAllSpawnModels();
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.render_off"]}");
                return;
            case "a" or "b":
                _renderMode = arg;
                PauseWarmupForEditing();
                SpawnService.ShowSpawns(_plugin, _plugin.MapConfigService.GetSpawnsClone(),
                    arg == "a" ? Bombsite.A : Bombsite.B);
                return;
            case "all" or "":
                _renderMode = "all";
                PauseWarmupForEditing();
                SpawnService.ShowAllSpawns(_plugin, _plugin.MapConfigService.GetSpawnsClone());
                return;
            case "flag":
                var flag = info.GetArg(2).ToLowerInvariant();
                if (flag.Length == 0)
                {
                    info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.spawns_usage"]}");
                    return;
                }

                _renderMode = $"flag:{flag}";
                PauseWarmupForEditing();
                SpawnService.ShowAllSpawns(_plugin, _plugin.MapConfigService.GetSpawnsClone(), flag);
                return;
            default:
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.spawns_usage"]}");
                return;
        }
    }

    private static void PauseWarmupForEditing()
    {
        Server.ExecuteCommand("mp_warmup_pausetimer 1");
        Server.ExecuteCommand("mp_warmuptime 999999");
        Server.ExecuteCommand("mp_warmup_start");
    }

    private void RefreshRender()
    {
        if (_renderMode == "off" || _plugin.MapConfigService is null)
        {
            return;
        }

        var spawns = _plugin.MapConfigService.GetSpawnsClone();
        if (_renderMode is "a" or "b")
        {
            SpawnService.ShowSpawns(_plugin, spawns, _renderMode == "a" ? Bombsite.A : Bombsite.B);
        }
        else if (_renderMode.StartsWith("flag:"))
        {
            SpawnService.ShowAllSpawns(_plugin, spawns, _renderMode[5..]);
        }
        else
        {
            SpawnService.ShowAllSpawns(_plugin, spawns);
        }
    }

    // ---------- editing ----------

    private void OnSpawnCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!_admin.Require(player, info, AdminLevel.Admin) || !PlayerHelper.IsValid(player))
        {
            return;
        }

        if (_plugin.MapConfigService is null || _plugin.SpawnManager is null)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.not_ready"]}");
            return;
        }

        var action = info.GetArg(1).ToLowerInvariant();
        switch (action)
        {
            case "add":
                HandleAdd(player!, info);
                return;
            case "del" or "delete" or "remove":
                HandleDelete(player!, info);
                return;
            case "move":
                HandleMove(player!, info);
                return;
            case "flag":
                HandleFlag(player!, info);
                return;
            case "info":
                HandleInfo(player!, info);
                return;
            case "test":
                HandleTest(player!, info);
                return;
            case "round":
                HandleRound(player!, info);
                return;
            default:
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.usage"]}");
                return;
        }
    }

    private void HandleAdd(CCSPlayerController player, CommandInfo info)
    {
        if (!PlayerHelper.HasAlivePawn(player))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.need_alive"]}");
            return;
        }

        var teamArg = info.GetArg(2).ToUpperInvariant();
        var siteArg = info.GetArg(3).ToUpperInvariant();
        if (teamArg is not ("T" or "CT") || siteArg is not ("A" or "B"))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.add_usage"]}");
            return;
        }

        var pawn = player.PlayerPawn.Value!;
        var flags = new List<string>();
        var canBePlanter = teamArg == "T" && pawn.InBombZoneTrigger;
        for (var i = 4; i < info.ArgCount; i++)
        {
            var flag = info.GetArg(i).ToLowerInvariant();
            if (flag == "planter")
            {
                canBePlanter = true;
            }
            else if (KnownFlags.Contains(flag) && !flags.Contains(flag))
            {
                flags.Add(flag);
            }
            else
            {
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.unknown_flag",
                    flag, string.Join(", ", KnownFlags) + ", planter"]}");
                return;
            }
        }

        var newSpawn = new Spawn(
            vector: new Vector(pawn.AbsOrigin!.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z),
            qAngle: new QAngle(pawn.AbsRotation!.X, pawn.AbsRotation.Y, pawn.AbsRotation.Z))
        {
            Team = teamArg == "T" ? CsTeam.Terrorist : CsTeam.CounterTerrorist,
            Bombsite = siteArg == "A" ? Bombsite.A : Bombsite.B,
            CanBePlanter = canBePlanter,
            Flags = flags,
            AddedBy = player.PlayerName,
        };

        // Refuse near-duplicates (same rule as the base editor).
        var nearest = FindNearest(pawn.AbsOrigin!, out var distance);
        if (nearest is not null && distance <= 72)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.too_close"]}");
            return;
        }

        if (!_plugin.MapConfigService!.AddSpawn(newSpawn))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.add_failed"]}");
            return;
        }

        _plugin.SpawnManager!.CalculateMapSpawns();
        RefreshRender();
        Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.spawn.added",
            player.PlayerName, teamArg, siteArg,
            flags.Count > 0 ? $" [{string.Join(",", flags)}]" : ""]}");
        Logger.LogInfo("Garden/SpawnEditor",
            $"{player.PlayerName} added {teamArg} spawn @{siteArg} flags=[{string.Join(",", flags)}]");
    }

    private void HandleDelete(CCSPlayerController player, CommandInfo info)
    {
        var origin = player.PlayerPawn.Value?.AbsOrigin;
        var nearest = origin is null ? null : FindNearest(origin, out _);
        if (nearest is null)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.none_near"]}");
            return;
        }

        if (!_plugin.MapConfigService!.RemoveSpawn(nearest))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.del_failed"]}");
            return;
        }

        _plugin.SpawnManager!.CalculateMapSpawns();
        RefreshRender();
        Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.spawn.deleted",
            player.PlayerName, TeamLabel(nearest), nearest.Bombsite]}");
        Logger.LogInfo("Garden/SpawnEditor",
            $"{player.PlayerName} deleted {nearest.Team} spawn @{nearest.Bombsite} (added by {nearest.AddedBy ?? "?"})");
    }

    private void HandleMove(CCSPlayerController player, CommandInfo info)
    {
        if (!PlayerHelper.HasAlivePawn(player))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.need_alive"]}");
            return;
        }

        var pawn = player.PlayerPawn.Value!;
        var nearest = FindNearest(pawn.AbsOrigin!, out _);
        if (nearest is null)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.none_near"]}");
            return;
        }

        _plugin.MapConfigService!.MutateSpawns(_ =>
        {
            nearest.Vector.X = pawn.AbsOrigin!.X;
            nearest.Vector.Y = pawn.AbsOrigin.Y;
            nearest.Vector.Z = pawn.AbsOrigin.Z;
            nearest.QAngle.X = pawn.AbsRotation!.X;
            nearest.QAngle.Y = pawn.AbsRotation.Y;
            nearest.QAngle.Z = pawn.AbsRotation.Z;
        });

        _plugin.SpawnManager!.CalculateMapSpawns();
        RefreshRender();
        Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.spawn.moved",
            player.PlayerName, TeamLabel(nearest), nearest.Bombsite]}");
    }

    private void HandleFlag(CCSPlayerController player, CommandInfo info)
    {
        var flag = info.GetArg(2).ToLowerInvariant();
        if (flag != "planter" && !KnownFlags.Contains(flag))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.unknown_flag",
                flag, string.Join(", ", KnownFlags) + ", planter"]}");
            return;
        }

        var origin = player.PlayerPawn.Value?.AbsOrigin;
        var nearest = origin is null ? null : FindNearest(origin, out _);
        if (nearest is null)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.none_near"]}");
            return;
        }

        var enabled = false;
        _plugin.MapConfigService!.MutateSpawns(_ =>
        {
            if (flag == "planter")
            {
                nearest.CanBePlanter = !nearest.CanBePlanter;
                enabled = nearest.CanBePlanter;
            }
            else if (nearest.Flags.Contains(flag))
            {
                nearest.Flags.Remove(flag);
            }
            else
            {
                nearest.Flags.Add(flag);
                enabled = true;
            }
        });

        _plugin.SpawnManager!.CalculateMapSpawns();
        RefreshRender();
        info.ReplyToCommand($"{Prefix} {_plugin.Localizer[
            enabled ? "garden.spawn.flag_on" : "garden.spawn.flag_off",
            flag, TeamLabel(nearest), nearest.Bombsite]}");
    }

    private void HandleInfo(CCSPlayerController player, CommandInfo info)
    {
        var origin = player.PlayerPawn.Value?.AbsOrigin;
        var nearest = origin is null ? null : FindNearest(origin, out var distance);
        if (nearest is null)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.none_near"]}");
            return;
        }

        info.ReplyToCommand($"{Prefix} {TeamLabel(nearest)} @{nearest.Bombsite}" +
                            (nearest.CanBePlanter ? " [planter]" : "") +
                            (nearest.Flags.Count > 0 ? $" [{string.Join(",", nearest.Flags)}]" : "") +
                            $" — by {nearest.AddedBy ?? "?"} — " +
                            $"({nearest.Vector.X:F0} {nearest.Vector.Y:F0} {nearest.Vector.Z:F0})");
    }

    // ---------- quick testing ----------

    private void HandleTest(CCSPlayerController player, CommandInfo info)
    {
        if (!PlayerHelper.HasAlivePawn(player))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.need_alive"]}");
            return;
        }

        var filter = info.GetArg(2).ToLowerInvariant();
        var spawns = _plugin.MapConfigService!.GetSpawnsClone();
        if (filter is "a" or "b")
        {
            var site = filter == "a" ? Bombsite.A : Bombsite.B;
            spawns = spawns.Where(s => s.Bombsite == site).ToList();
        }

        if (spawns.Count == 0)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.none_near"]}");
            return;
        }

        var steamId = player.SteamID;
        var cursor = _testCursor.TryGetValue(steamId, out var current) ? current + 1 : 0;
        if (cursor >= spawns.Count)
        {
            cursor = 0;
        }

        _testCursor[steamId] = cursor;

        var spawn = spawns[cursor];
        player.PlayerPawn.Value!.Teleport(spawn.Vector, spawn.QAngle, new Vector(0, 0, 0));
        info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.test",
            cursor + 1, spawns.Count, TeamLabel(spawn), spawn.Bombsite]}");
    }

    private void HandleRound(CCSPlayerController player, CommandInfo info)
    {
        if (_plugin.RoundHandlers is null)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.not_ready"]}");
            return;
        }

        var arg = info.GetArg(2).ToLowerInvariant();
        switch (arg)
        {
            case "a" or "b":
                _plugin.RoundHandlers.SetForcedBombsite(arg == "a" ? Bombsite.A : Bombsite.B);
                Server.ExecuteCommand("mp_warmup_pausetimer 0");
                Server.ExecuteCommand("mp_warmup_end");
                Server.PrintToChatAll($"{Prefix} {_plugin.Localizer["garden.spawn.round_forced",
                    arg.ToUpperInvariant(), player.PlayerName]}");
                // Skip straight into a fresh round on the forced site.
                GameRulesHelper.TerminateRound(RoundEndReason.RoundDraw);
                return;
            case "off":
                _plugin.RoundHandlers.SetForcedBombsite(null);
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.round_off"]}");
                return;
            default:
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.spawn.round_usage"]}");
                return;
        }
    }

    // ---------- helpers ----------

    private Spawn? FindNearest(Vector origin, out double distance)
    {
        Spawn? nearest = null;
        distance = NearestMaxDistance;

        foreach (var spawn in _plugin.MapConfigService!.GetSpawnsClone())
        {
            var d = GameRulesHelper.GetDistanceBetweenVectors(spawn.Vector, origin);
            if (d < distance)
            {
                distance = d;
                nearest = spawn;
            }
        }

        return nearest;
    }

    private static string TeamLabel(Spawn spawn) =>
        spawn.Team == CsTeam.Terrorist ? "T" : "CT";
}
