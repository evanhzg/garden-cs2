using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRankingsCore.Db;
using CounterStrikeSharp.API;

namespace GardenRankingsCore.Managers;

public static class HeatmapCollector
{
    public static string? ActiveSite { get; set; }

    public static void RecordDeath(EventPlayerDeath @event, bool isRanked)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;

        if (victim?.PlayerPawn?.Value == null || attacker?.PlayerPawn?.Value == null) return;
        if (!victim.IsValid || !attacker.IsValid) return;

        var victimPawn = victim.PlayerPawn.Value;
        var attackerPawn = attacker.PlayerPawn.Value;

        var victimId = victim.SteamID;
        var attackerId = attacker.SteamID;
        var weapon = @event.Weapon ?? "unknown";
        var headshot = @event.Headshot;
        var mapName = Server.MapName;

        var vX = victimPawn.AbsOrigin?.X ?? 0f;
        var vY = victimPawn.AbsOrigin?.Y ?? 0f;
        var vZ = victimPawn.AbsOrigin?.Z ?? 0f;

        var aX = attackerPawn.AbsOrigin?.X ?? 0f;
        var aY = attackerPawn.AbsOrigin?.Y ?? 0f;
        var aZ = attackerPawn.AbsOrigin?.Z ?? 0f;

        Task.Run(() =>
        {
            try
            {
                using var db = new Db.Db();
                db.GardenHeatmaps.Add(new GardenHeatmap
                {
                    VictimSteamId = victimId,
                    AttackerSteamId = attackerId,
                    MapName = mapName,
                    VictimX = vX,
                    VictimY = vY,
                    VictimZ = vZ,
                    AttackerX = aX,
                    AttackerY = aY,
                    AttackerZ = aZ,
                    Weapon = weapon,
                    IsHeadshot = headshot,
                    IsRanked = isRanked,
                    Site = ActiveSite,
                    CreatedAtUtc = DateTime.UtcNow
                });
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Garden Heatmaps] Failed to save heatmap point: {ex.Message}");
            }
        });
    }
}
