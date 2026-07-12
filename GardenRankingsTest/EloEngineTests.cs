using GardenRankingsCore.Config;
using GardenRankingsCore.Models;
using GardenRankingsCore.Rating;

namespace GardenRankingsTest;

public class EloEngineTests : BaseTestFixture
{
    private static RoundContext RankedCtx(int winner = TeamNums.T)
    {
        return new RoundContext
        {
            Map = "de_dust2",
            RoundType = RetakesRoundType.FullBuy,
            IsRanked = true,
            TPlayerCount = 2,
            CtPlayerCount = 2,
            WinnerTeamNum = winner,
        };
    }

    private static List<PlayerRoundStats> TwoVsTwo(int elo = 5000)
    {
        return new List<PlayerRoundStats>
        {
            new() {SteamId = 1, TeamNum = TeamNums.T, EloBefore = elo, Rating = 1.0},
            new() {SteamId = 2, TeamNum = TeamNums.T, EloBefore = elo, Rating = 1.0},
            new() {SteamId = 3, TeamNum = TeamNums.Ct, EloBefore = elo, Rating = 1.0},
            new() {SteamId = 4, TeamNum = TeamNums.Ct, EloBefore = elo, Rating = 1.0},
        };
    }

    [Test]
    public void WinnersGainLosersLose()
    {
        var players = TwoVsTwo();
        EloEngine.ApplyEloDeltas(players, RankedCtx(winner: TeamNums.Ct));

        Assert.Multiple(() =>
        {
            Assert.That(players.Where(p => p.TeamNum == TeamNums.Ct).All(p => p.EloDelta > 0), Is.True);
            Assert.That(players.Where(p => p.TeamNum == TeamNums.T).All(p => p.EloDelta < 0), Is.True);
        });
    }

    [Test]
    public void UnrankedRoundsNeverChangeElo()
    {
        var players = TwoVsTwo();
        var ctx = RankedCtx();
        ctx.IsRanked = false;

        EloEngine.ApplyEloDeltas(players, ctx);

        Assert.That(players.All(p => p.EloDelta == 0 && p.EloAfter == p.EloBefore), Is.True);
    }

    [Test]
    public void RetakesAsymmetryMakesCtWinsWorthMore()
    {
        // Ts are expected to win more often, so a CT round win moves more ELO
        // than a T round win, all else equal.
        var tWin = TwoVsTwo();
        EloEngine.ApplyEloDeltas(tWin, RankedCtx(winner: TeamNums.T));

        var ctWin = TwoVsTwo();
        EloEngine.ApplyEloDeltas(ctWin, RankedCtx(winner: TeamNums.Ct));

        var tWinnerGain = tWin.First(p => p.TeamNum == TeamNums.T).EloDelta;
        var ctWinnerGain = ctWin.First(p => p.TeamNum == TeamNums.Ct).EloDelta;

        Assert.That(ctWinnerGain, Is.GreaterThan(tWinnerGain));
    }

    [Test]
    public void AfkPlayersKeepTheirElo()
    {
        var players = TwoVsTwo();
        players[0].WasAfk = true;

        EloEngine.ApplyEloDeltas(players, RankedCtx(winner: TeamNums.T));

        Assert.Multiple(() =>
        {
            Assert.That(players[0].EloDelta, Is.EqualTo(0));
            Assert.That(players[1].EloDelta, Is.Not.EqualTo(0));
        });
    }

    [Test]
    public void TeamKilledLossIsFullyMitigatedByDefault()
    {
        var players = TwoVsTwo();
        var victim = players.First(p => p.TeamNum == TeamNums.T);
        victim.WasTeamKilled = true;

        EloEngine.ApplyEloDeltas(players, RankedCtx(winner: TeamNums.Ct));

        var otherLoser = players.First(p => p.TeamNum == TeamNums.T && p != victim);
        Assert.Multiple(() =>
        {
            Assert.That(victim.EloDelta, Is.EqualTo(0));
            Assert.That(otherLoser.EloDelta, Is.LessThan(0));
        });
    }

    [Test]
    public void EarlyDeathAndEnemyGreatRoundReduceLoss()
    {
        // Baseline loss
        var baseline = TwoVsTwo();
        EloEngine.ApplyEloDeltas(baseline, RankedCtx(winner: TeamNums.Ct));
        var baselineLoss = Math.Abs(baseline.First(p => p.TeamNum == TeamNums.T).EloDelta);

        // Early death mitigation
        var early = TwoVsTwo();
        var earlyVictim = early.First(p => p.TeamNum == TeamNums.T);
        earlyVictim.Died = true;
        earlyVictim.DiedAtSeconds = 3;
        earlyVictim.Damage = 0;
        EloEngine.FlagEarlyDeaths(early);
        EloEngine.ApplyEloDeltas(early, RankedCtx(winner: TeamNums.Ct));
        Assert.That(Math.Abs(earlyVictim.EloDelta), Is.LessThan(baselineLoss));

        // Enemy great round mitigation
        var greatRound = TwoVsTwo();
        greatRound.First(p => p.TeamNum == TeamNums.Ct).Kills =
            Configs.GetConfigData().Elo.EnemyGreatRoundKills;
        EloEngine.ApplyEloDeltas(greatRound, RankedCtx(winner: TeamNums.Ct));
        var mitigatedLoss = Math.Abs(greatRound.First(p => p.TeamNum == TeamNums.T).EloDelta);
        Assert.That(mitigatedLoss, Is.LessThan(baselineLoss));
    }

    [Test]
    public void HigherRatedWinnersGainMore()
    {
        var players = TwoVsTwo();
        players.First(p => p.SteamId == 3).Rating = 2.0;
        players.First(p => p.SteamId == 4).Rating = 0.3;

        EloEngine.ApplyEloDeltas(players, RankedCtx(winner: TeamNums.Ct));

        Assert.That(
            players.First(p => p.SteamId == 3).EloDelta,
            Is.GreaterThan(players.First(p => p.SteamId == 4).EloDelta));
    }

    [Test]
    public void ExpectationRespectsTeamSizeAdvantage()
    {
        var bigger = EloEngine.ExpectedWinProbability(
            TeamNums.T, 5000, 5000, 5, 3, RetakesRoundType.FullBuy);
        var smaller = EloEngine.ExpectedWinProbability(
            TeamNums.T, 5000, 5000, 3, 5, RetakesRoundType.FullBuy);

        Assert.That(bigger, Is.GreaterThan(smaller));
    }

    [Test]
    public void ExpectationRespectsEloDifference()
    {
        var favored = EloEngine.ExpectedWinProbability(
            TeamNums.Ct, 8000, 5000, 5, 5, RetakesRoundType.FullBuy);
        var underdog = EloEngine.ExpectedWinProbability(
            TeamNums.Ct, 5000, 8000, 5, 5, RetakesRoundType.FullBuy);

        Assert.That(favored, Is.GreaterThan(underdog));
    }
}
