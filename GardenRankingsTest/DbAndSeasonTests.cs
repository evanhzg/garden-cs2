using GardenRankingsCore.Config;
using GardenRankingsCore.Db;
using GardenRankingsCore.Managers;
using GardenRankingsCore.Models;

namespace GardenRankingsTest;

public class DbAndSeasonTests : BaseTestFixture
{
    private static RoundContext RankedCtx(int winner = TeamNums.T)
    {
        return new RoundContext
        {
            Map = "de_inferno",
            StartedAtUtc = DateTime.UtcNow,
            RoundType = RetakesRoundType.FullBuy,
            IsRanked = true,
            TPlayerCount = 2,
            CtPlayerCount = 2,
            WinnerTeamNum = winner,
        };
    }

    private static List<PlayerRoundStats> SamplePlayers()
    {
        return new List<PlayerRoundStats>
        {
            new()
            {
                SteamId = 11, PlayerName = "Alpha", TeamNum = TeamNums.T, EloBefore = 5000,
                Kills = 2, Headshots = 1, Damage = 180, Rating = 1.5, EloDelta = 20, EloAfter = 5020,
                WonRound = true,
            },
            new()
            {
                SteamId = 12, PlayerName = "Bravo", TeamNum = TeamNums.Ct, EloBefore = 5000,
                Died = true, Damage = 40, Rating = 0.5, EloDelta = -15, EloAfter = 4985,
            },
        };
    }

    [Test]
    public void PersistRoundRoundtrip()
    {
        var seasonId = Queries.GetActiveSeason().Id;
        var updated = Queries.PersistRound(seasonId, RankedCtx(), SamplePlayers());

        Assert.Multiple(() =>
        {
            Assert.That(updated[11].Elo, Is.EqualTo(5020));
            Assert.That(updated[11].PeakElo, Is.EqualTo(5020));
            Assert.That(updated[11].RankedRoundsPlayed, Is.EqualTo(1));
            Assert.That(updated[11].RankedRoundsWon, Is.EqualTo(1));
            Assert.That(updated[12].Elo, Is.EqualTo(4985));
            // Peak never goes below the starting elo baseline
            Assert.That(updated[12].PeakElo, Is.EqualTo(5000));
        });

        var db = Db.GetInstance();
        Assert.Multiple(() =>
        {
            Assert.That(db.RoundRecords.Count(), Is.EqualTo(1));
            Assert.That(db.PlayerRoundRecords.Count(), Is.EqualTo(2));
        });

        var summary = Queries.GetPlayerSeasonSummary(seasonId, 11);
        Assert.Multiple(() =>
        {
            Assert.That(summary.Kills, Is.EqualTo(2));
            Assert.That(summary.RoundsPlayed, Is.EqualTo(1));
            Assert.That(summary.AverageRating, Is.EqualTo(1.5).Within(0.001));
        });
    }

    [Test]
    public void PlacementOrdersByElo()
    {
        var seasonId = Queries.GetActiveSeason().Id;
        Queries.PersistRound(seasonId, RankedCtx(), SamplePlayers());

        var first = Queries.GetPlacement(seasonId, 11);
        var second = Queries.GetPlacement(seasonId, 12);
        var unknown = Queries.GetPlacement(seasonId, 999);

        Assert.Multiple(() =>
        {
            Assert.That(first!.Rank, Is.EqualTo(1));
            Assert.That(second!.Rank, Is.EqualTo(2));
            Assert.That(first.TotalRanked, Is.EqualTo(2));
            Assert.That(unknown, Is.Null);
        });
    }

    [Test]
    public void UnrankedRoundsDoNotTouchEloOrPlacement()
    {
        var seasonId = Queries.GetActiveSeason().Id;
        var ctx = RankedCtx();
        ctx.IsRanked = false;

        var players = SamplePlayers();
        foreach (var p in players)
        {
            p.EloDelta = 0;
            p.EloAfter = p.EloBefore;
        }

        var updated = Queries.PersistRound(seasonId, ctx, players);

        Assert.Multiple(() =>
        {
            Assert.That(updated[11].Elo, Is.EqualTo(Configs.GetConfigData().Elo.StartingElo));
            Assert.That(updated[11].RankedRoundsPlayed, Is.EqualTo(0));
            Assert.That(updated[11].UnrankedRoundsPlayed, Is.EqualTo(1));
            Assert.That(Queries.GetPlacement(seasonId, 11), Is.Null);
        });
    }

    [Test]
    public void SeasonRolloverArchivesAndRecordsAreDetected()
    {
        var firstSeason = Queries.GetActiveSeason();
        Queries.PersistRound(firstSeason.Id, RankedCtx(), SamplePlayers());

        var newSeason = Queries.StartNewSeason("Season Two");
        Assert.Multiple(() =>
        {
            Assert.That(newSeason.Id, Is.Not.EqualTo(firstSeason.Id));
            Assert.That(Queries.GetAllSeasons().Count(s => s.IsActive), Is.EqualTo(1));
            // Old season data is still there
            Assert.That(Queries.GetPlacement(firstSeason.Id, 11), Is.Not.Null);
            // New season is a clean slate
            Assert.That(Queries.GetPlacement(newSeason.Id, 11), Is.Null);
        });

        // Previous-season records
        var serverBest = Queries.GetPreviousSeasonsServerBest();
        Assert.Multiple(() =>
        {
            Assert.That(serverBest, Is.Not.Null);
            Assert.That(serverBest!.PeakElo, Is.EqualTo(5020));
            Assert.That(Queries.GetPreviousSeasonsPersonalBest(11), Is.EqualTo(5020));
            Assert.That(Queries.GetPreviousSeasonsPersonalBest(999), Is.Null);
        });

        // SeasonManager announces when the record is beaten, once per improvement
        SeasonManager.Instance.Initialize();
        SeasonManager.Instance.PreloadPersonalBest(11);

        var below = SeasonManager.Instance.CheckRecords(11, 5010);
        Assert.That(below.ServerRecordBroken, Is.False);

        var broken = SeasonManager.Instance.CheckRecords(11, 5100);
        Assert.Multiple(() =>
        {
            Assert.That(broken.ServerRecordBroken, Is.True);
            Assert.That(broken.PersonalBestBroken, Is.True);
            Assert.That(broken.PreviousServerRecord, Is.EqualTo(5020));
        });

        // Same value again: no re-announcement
        var repeat = SeasonManager.Instance.CheckRecords(11, 5100);
        Assert.Multiple(() =>
        {
            Assert.That(repeat.ServerRecordBroken, Is.False);
            Assert.That(repeat.PersonalBestBroken, Is.False);
        });
    }

    [Test]
    public void ProfilesAreUpserted()
    {
        Queries.UpsertPlayerProfile(42, "OldName");
        Queries.UpsertPlayerProfile(42, "NewName");

        var db = Db.GetInstance();
        var profile = db.PlayerProfiles.Single(p => p.SteamId == 42);
        Assert.That(profile.LastKnownName, Is.EqualTo("NewName"));
    }
}
