using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using GardenRankingsCore.Db;
using GardenRankingsCore.Managers;
using GardenRankingsCore.Rating;
using GardenRetakes.Core.GameModes;

namespace GardenRankings;

public static class LiveMatchBroadcaster
{
    private static bool _isBroadcasting;

    public static void BroadcastAsync(GameModeKind mode, CompetitiveMatchManager cr, RankedStateManager ranked, Dictionary<(ulong Killer, ulong Victim), int> sessionKills, Dictionary<ulong, int> eloCache)
    {
        if (_isBroadcasting) return;
        _isBroadcasting = true;

        var mapName = Server.MapName;
        var isCr = mode == GameModeKind.Retakes && cr.IsLive;
        var isRanked = mode == GameModeKind.Retakes && ranked.IsActive;

        var modeName = mode switch
        {
            GameModeKind.Retakes => isCr ? "Competitive Retakes" : (isRanked ? "Ranked Retakes" : "Casual Retakes"),
            GameModeKind.Duels => "Duels",
            GameModeKind.Executes => "Executes",
            GameModeKind.FastStrat => "Fast Strat",
            GameModeKind.Edit => "Editor",
            _ => mode.ToString()
        };

        var teamAKey = cr.TeamAKey;
        var teamAName = cr.TeamAName;
        var teamASize = cr.TeamA.Count;
        var teamBKey = cr.TeamBKey;
        var teamBName = cr.TeamBName;
        var teamBSize = cr.TeamB.Count;
        var scoreA = cr.ScoreA;
        var scoreB = cr.ScoreB;
        var seasonId = SeasonManager.Instance.ActiveSeasonId;

        // Take snapshot of players
        var players = Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot && p.ActionTrackingServices != null)
            .Select(p =>
            {
                var steamId = p.SteamID;
                var roster = cr.RosterOf(steamId);
                var teamStr = isCr ? (roster ?? "Spectator") : p.TeamNum.ToString(); // In non-CR, team string doesn't matter much or can be just T/CT
                var elo = eloCache.TryGetValue(steamId, out var e) ? e : 1000;
                return new
                {
                    SteamId = steamId.ToString(),
                    Name = p.PlayerName,
                    Team = teamStr,
                    Kills = p.ActionTrackingServices!.MatchStats.Kills,
                    Deaths = p.ActionTrackingServices.MatchStats.Deaths,
                    Assists = p.ActionTrackingServices.MatchStats.Assists,
                    Damage = p.ActionTrackingServices.MatchStats.Damage,
                    Elo = elo
                };
            })
            .Where(p => isCr ? p.Team != "Spectator" : true)
            .ToList();

        if (players.Count == 0)
        {
            Task.Run(async () =>
            {
                await ClearAsync();
                _isBroadcasting = false;
            });
            return;
        }

        // Head to head from sessionKills
        var h2h = sessionKills
            .Where(kvp => kvp.Value >= 3)
            .Select(kvp =>
            {
                var killer = players.FirstOrDefault(p => p.SteamId == kvp.Key.Killer.ToString());
                var victim = players.FirstOrDefault(p => p.SteamId == kvp.Key.Victim.ToString());
                return new
                {
                    KillerName = killer?.Name ?? "Unknown",
                    VictimName = victim?.Name ?? "Unknown",
                    Kills = kvp.Value
                };
            })
            .Where(x => x.KillerName != "Unknown" && x.VictimName != "Unknown")
            .OrderByDescending(x => x.Kills)
            .Take(10)
            .ToList();

        Task.Run(async () =>
        {
            try
            {
                string winPredA = "";
                string winPredB = "";

                if (isCr && teamASize > 0 && teamBSize > 0)
                {
                    var teamA = Queries.GetOrCreateCrTeam(seasonId, teamAKey, teamAName, teamASize);
                    var teamB = Queries.GetOrCreateCrTeam(seasonId, teamBKey, teamBName, teamBSize);

                    var (deltaA_Win, _) = TeamEloEngine.ComputeMatchDeltas(teamA.Elo, teamB.Elo, 1.0);
                    var (deltaA_Loss, _) = TeamEloEngine.ComputeMatchDeltas(teamA.Elo, teamB.Elo, 0.0);
                    
                    var (deltaB_Win, _) = TeamEloEngine.ComputeMatchDeltas(teamB.Elo, teamA.Elo, 1.0);
                    var (deltaB_Loss, _) = TeamEloEngine.ComputeMatchDeltas(teamB.Elo, teamA.Elo, 0.0);

                    winPredA = $"+{deltaA_Win} / {deltaA_Loss}";
                    winPredB = $"+{deltaB_Win} / {deltaB_Loss}";
                }

                var matchState = new
                {
                    Map = mapName,
                    Mode = modeName,
                    IsCr = isCr,
                    IsRanked = isRanked,
                    TeamAName = teamAName,
                    TeamBName = teamBName,
                    ScoreA = scoreA,
                    ScoreB = scoreB,
                    WinPredictionA = winPredA,
                    WinPredictionB = winPredB,
                    Players = players,
                    HeadToHead = h2h
                };

                var json = JsonSerializer.Serialize(matchState);

                await using var db = new Db();
                var row = await db.WebLiveMatches.FindAsync(1);
                if (row == null)
                {
                    row = new WebLiveMatch { ServerId = 1, Data = json, UpdatedAtUtc = DateTime.UtcNow };
                    db.WebLiveMatches.Add(row);
                }
                else
                {
                    row.Data = json;
                    row.UpdatedAtUtc = DateTime.UtcNow;
                }

                await db.SaveChangesAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[LiveMatchBroadcaster] Failed to update live match state: {e}");
            }
            finally
            {
                _isBroadcasting = false;
            }
        });
    }

    public static async Task ClearAsync()
    {
        try
        {
            await using var db = new Db();
            var row = await db.WebLiveMatches.FindAsync(1);
            if (row != null)
            {
                db.WebLiveMatches.Remove(row);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[LiveMatchBroadcaster] Failed to clear live match state: {e}");
        }
    }
}
