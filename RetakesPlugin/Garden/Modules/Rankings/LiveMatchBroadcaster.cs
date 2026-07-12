using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using GardenRankingsCore.Db;
using GardenRankingsCore.Managers;
using GardenRankingsCore.Rating;

namespace GardenRankings;

public static class LiveMatchBroadcaster
{
    public static void TriggerUpdate(CompetitiveMatchManager cr)
    {
        if (!cr.IsLive)
        {
            Task.Run(ClearAsync);
            return;
        }

        var mapName = Server.MapName;
        var seasonId = SeasonManager.Instance.ActiveSeasonId;
        var teamAKey = cr.TeamAKey;
        var teamAName = cr.TeamAName;
        var teamASize = cr.TeamA.Count;
        var teamBKey = cr.TeamBKey;
        var teamBName = cr.TeamBName;
        var teamBSize = cr.TeamB.Count;
        var scoreA = cr.ScoreA;
        var scoreB = cr.ScoreB;

        var players = Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot && p.ActionTrackingServices != null)
            .Select(p =>
            {
                var steamId = p.SteamID;
                var roster = cr.RosterOf(steamId);
                return new
                {
                    SteamId = steamId.ToString(),
                    Name = p.PlayerName,
                    Team = roster ?? "Spectator",
                    Kills = p.ActionTrackingServices!.MatchStats.Kills,
                    Deaths = p.ActionTrackingServices.MatchStats.Deaths,
                    Assists = p.ActionTrackingServices.MatchStats.Assists,
                    Damage = p.ActionTrackingServices.MatchStats.Damage
                };
            })
            .Where(p => p.Team != "Spectator")
            .ToList();

        Task.Run(async () =>
        {
            try
            {
                var teamA = Queries.GetOrCreateCrTeam(seasonId, teamAKey, teamAName, teamASize);
                var teamB = Queries.GetOrCreateCrTeam(seasonId, teamBKey, teamBName, teamBSize);

                var (deltaA_Win, _) = TeamEloEngine.ComputeMatchDeltas(teamA.Elo, teamB.Elo, 1.0);
                var (deltaA_Loss, _) = TeamEloEngine.ComputeMatchDeltas(teamA.Elo, teamB.Elo, 0.0);
                
                var (deltaB_Win, _) = TeamEloEngine.ComputeMatchDeltas(teamB.Elo, teamA.Elo, 1.0);
                var (deltaB_Loss, _) = TeamEloEngine.ComputeMatchDeltas(teamB.Elo, teamA.Elo, 0.0);

                var matchState = new
                {
                    Map = mapName,
                    IsCr = true,
                    TeamAName = teamAName,
                    TeamBName = teamBName,
                    ScoreA = scoreA,
                    ScoreB = scoreB,
                    WinPredictionA = $"+{deltaA_Win} / {deltaA_Loss}",
                    WinPredictionB = $"+{deltaB_Win} / {deltaB_Loss}",
                    Players = players
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
