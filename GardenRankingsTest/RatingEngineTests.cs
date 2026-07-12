using GardenRankingsCore.Models;
using GardenRankingsCore.Rating;

namespace GardenRankingsTest;

public class RatingEngineTests : BaseTestFixture
{
    private static RoundContext Ctx(RetakesRoundType type = RetakesRoundType.FullBuy, int t = 5, int ct = 5)
    {
        return new RoundContext
        {
            Map = "de_mirage",
            RoundType = type,
            TPlayerCount = t,
            CtPlayerCount = ct,
            WinnerTeamNum = TeamNums.T,
        };
    }

    private static PlayerRoundStats AverageRound()
    {
        // Roughly the configured "average" 5v5 round: ~0.65 kills, ~85 dmg, survives
        // about half the time (represented here as died-but-traded for KAST).
        return new PlayerRoundStats
        {
            SteamId = 1,
            TeamNum = TeamNums.T,
            Kills = 1,
            Damage = 85,
            Died = true,
            TradedDeath = true,
        };
    }

    [Test]
    public void AverageRoundRatesNearOne()
    {
        var rating = RatingEngine.ComputeRoundRating(AverageRound(), Ctx());
        Assert.That(rating, Is.InRange(0.6, 1.6));
    }

    [Test]
    public void BigRoundRatesWellAboveAverageRound()
    {
        var big = new PlayerRoundStats
        {
            SteamId = 1, TeamNum = TeamNums.Ct, Kills = 4, Headshots = 2, Damage = 380,
            OpeningKill = true, Defused = true, ClutchVersus = 2, ClutchWon = true,
        };
        var zero = new PlayerRoundStats
        {
            SteamId = 2, TeamNum = TeamNums.Ct, Died = true,
        };

        var bigRating = RatingEngine.ComputeRoundRating(big, Ctx());
        var zeroRating = RatingEngine.ComputeRoundRating(zero, Ctx());

        Assert.That(bigRating, Is.GreaterThan(2.0));
        Assert.That(zeroRating, Is.LessThan(0.5));
        Assert.That(bigRating, Is.GreaterThan(zeroRating * 3));
    }

    [Test]
    public void KillsAreWorthMoreAgainstFewerEnemies()
    {
        var stats1 = new PlayerRoundStats {SteamId = 1, TeamNum = TeamNums.T, Kills = 2, Damage = 200};
        var stats2 = new PlayerRoundStats {SteamId = 1, TeamNum = TeamNums.T, Kills = 2, Damage = 200};

        var vs5 = RatingEngine.ComputeRoundRating(stats1, Ctx(t: 5, ct: 5));
        var vs3 = RatingEngine.ComputeRoundRating(stats2, Ctx(t: 3, ct: 3));

        Assert.That(vs3, Is.GreaterThan(vs5));
    }

    [Test]
    public void ObjectivesGiveImpact()
    {
        var without = new PlayerRoundStats {SteamId = 1, TeamNum = TeamNums.Ct, Kills = 1, Damage = 100};
        var with = new PlayerRoundStats {SteamId = 1, TeamNum = TeamNums.Ct, Kills = 1, Damage = 100, Defused = true};

        Assert.That(
            RatingEngine.ComputeRoundRating(with, Ctx()),
            Is.GreaterThan(RatingEngine.ComputeRoundRating(without, Ctx())));
    }

    [Test]
    public void AfkPlayersRateZero()
    {
        var players = new List<PlayerRoundStats>
        {
            new() {SteamId = 1, TeamNum = TeamNums.T, Kills = 3, WasAfk = true},
        };
        RatingEngine.ComputeRatings(players, Ctx());
        Assert.That(players[0].Rating, Is.EqualTo(0));
    }
}
