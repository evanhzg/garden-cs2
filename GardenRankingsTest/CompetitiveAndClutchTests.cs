using GardenRankingsCore.Config;
using GardenRankingsCore.Db;
using GardenRankingsCore.Managers;
using GardenRankingsCore.Models;
using GardenRankingsCore.Rating;

namespace GardenRankingsTest;

public class CompetitiveMatchManagerTests : BaseTestFixture
{
    private static CompetitiveMatchManager NewMatch()
    {
        var manager = new CompetitiveMatchManager();
        manager.StartMatch(
            new List<ulong> {1, 2}, new List<ulong> {3, 4}, "Alpha & Bravo", "Charlie & Delta");
        return manager;
    }

    [Test]
    public void FirstRoundOfEachHalfIsPistol()
    {
        var manager = NewMatch();

        var plan = manager.PlanNextRound();
        Assert.Multiple(() =>
        {
            Assert.That(plan.IsPistol, Is.True);
            Assert.That(plan.RoundTypeOrdinal, Is.EqualTo((int) RetakesRoundType.Pistol));
        });

        // Play out a full half; the next plan must be pistol again.
        for (var i = 0; i < 12; i++)
        {
            manager.RoundCompleted(TeamNums.T);
        }

        Assert.That(manager.PlanNextRound().IsPistol, Is.True);
    }

    [Test]
    public void ThreeLossesInARowTriggersForceBuyForTheLosingTeam()
    {
        var manager = NewMatch();

        // Team A (T side) wins three in a row -> Team B (CT side) force-buys.
        manager.RoundCompleted(TeamNums.T);
        manager.RoundCompleted(TeamNums.T);
        manager.RoundCompleted(TeamNums.T);

        var plan = manager.PlanNextRound();
        Assert.Multiple(() =>
        {
            Assert.That(plan.RoundTypeOrdinal, Is.EqualTo((int) RetakesRoundType.ForceBuy));
            Assert.That(plan.ForceBuyTeamNum, Is.EqualTo(TeamNums.Ct));
        });

        // A single win resets the streak.
        manager.RoundCompleted(TeamNums.Ct);
        Assert.That(manager.PlanNextRound().RoundTypeOrdinal, Is.EqualTo((int) RetakesRoundType.FullBuy));
    }

    [Test]
    public void HalftimeSwapsSidesAndMatchEndsAtThirteen()
    {
        var manager = NewMatch();
        Assert.That(manager.TeamASideTeamNum, Is.EqualTo(TeamNums.T));

        // 11 wins for A, 1 for B = 12 rounds -> halftime.
        for (var i = 0; i < 11; i++)
        {
            Assert.That(manager.RoundCompleted(TeamNums.T),
                Is.EqualTo(CrMatchEvent.None), $"round {i}");
        }

        Assert.That(manager.RoundCompleted(TeamNums.Ct), Is.EqualTo(CrMatchEvent.HalftimeReached));
        Assert.Multiple(() =>
        {
            Assert.That(manager.TeamASideTeamNum, Is.EqualTo(TeamNums.Ct));
            Assert.That(manager.CurrentHalf, Is.EqualTo(2));
            Assert.That(manager.ScoreA, Is.EqualTo(11));
            Assert.That(manager.ScoreB, Is.EqualTo(1));
        });

        // A now plays CT: two more wins take the match 13-1.
        Assert.That(manager.RoundCompleted(TeamNums.Ct), Is.EqualTo(CrMatchEvent.None));
        Assert.That(manager.RoundCompleted(TeamNums.Ct), Is.EqualTo(CrMatchEvent.MatchWonByA));
        Assert.That(manager.IsLive, Is.False);
    }

    [Test]
    public void TwelveAllIsADraw()
    {
        var manager = NewMatch();

        // Alternate wins: 12-12 after 24 rounds.
        CrMatchEvent lastEvent = CrMatchEvent.None;
        for (var round = 0; round < 24; round++)
        {
            // Team A wins even rounds regardless of the side it is on.
            var teamAWins = round % 2 == 0;
            var winnerSide = teamAWins ? manager.TeamASideTeamNum : manager.TeamBSideTeamNum;
            lastEvent = manager.RoundCompleted(winnerSide);
        }

        Assert.Multiple(() =>
        {
            Assert.That(lastEvent, Is.EqualTo(CrMatchEvent.MatchDraw));
            Assert.That(manager.ScoreA, Is.EqualTo(12));
            Assert.That(manager.ScoreB, Is.EqualTo(12));
        });
    }

    [Test]
    public void RosterLookupAndTeamKeys()
    {
        var manager = NewMatch();
        Assert.Multiple(() =>
        {
            Assert.That(manager.RosterOf(1), Is.EqualTo("A"));
            Assert.That(manager.RosterOf(4), Is.EqualTo("B"));
            Assert.That(manager.RosterOf(99), Is.Null);
            Assert.That(manager.TeamAKey, Is.EqualTo("1-2"));
            Assert.That(CompetitiveMatchManager.BuildTeamKey(new ulong[] {9, 3, 7}), Is.EqualTo("3-7-9"));
        });
    }
}

public class ClutchSchedulerTests : BaseTestFixture
{
    [Test]
    public void NoClutchesBelowMinimumPlayers()
    {
        var scheduler = new ClutchScheduler(new Random(42));
        for (var i = 0; i < 50; i++)
        {
            scheduler.OnRoundPlayed();
            Assert.That(scheduler.ShouldTriggerClutch(3), Is.False);
        }
    }

    [Test]
    public void GuaranteeForcesClutchesWhenRollsFailForever()
    {
        Configs.GetConfigData().Clutch.ChancePercent = 0;
        Configs.GetConfigData().Clutch.ForceAfterRounds = 5;
        Configs.GetConfigData().Clutch.MinPerMap = 2;

        var scheduler = new ClutchScheduler(new Random(42));
        scheduler.OnMapStart();

        var triggers = 0;
        for (var i = 0; i < 12; i++)
        {
            scheduler.OnRoundPlayed();
            if (scheduler.ShouldTriggerClutch(5))
            {
                scheduler.RegisterClutchRound();
                triggers++;
            }
        }

        // Forced at rounds 5 and 10.
        Assert.That(triggers, Is.EqualTo(2));
    }

    [Test]
    public void LayoutsAlwaysHaveMoreEnemiesThanClutchers()
    {
        var scheduler = new ClutchScheduler(new Random(1234));
        for (var players = 4; players <= 10; players++)
        {
            for (var i = 0; i < 50; i++)
            {
                var layout = scheduler.PickLayout(players);
                Assert.Multiple(() =>
                {
                    Assert.That(layout.EnemyCount, Is.GreaterThan(layout.ClutcherCount),
                        $"players={players}");
                    Assert.That(layout.ClutcherCount + layout.EnemyCount, Is.EqualTo(players));
                });
            }
        }
    }

    [Test]
    public void ExactlyMinimumPlayersMeansSoloClutchOnly()
    {
        var scheduler = new ClutchScheduler(new Random(7));
        for (var i = 0; i < 50; i++)
        {
            Assert.That(scheduler.PickLayout(4).ClutcherCount, Is.EqualTo(1));
        }
    }
}

public class CrPersistenceTests : BaseTestFixture
{
    [Test]
    public void CrMatchPersistsAndMovesTeamElo()
    {
        var seasonId = Queries.GetActiveSeason().Id;
        var teamA = Queries.GetOrCreateCrTeam(seasonId, "1-2", "Alpha & Bravo", 2);
        var teamB = Queries.GetOrCreateCrTeam(seasonId, "3-4", "Charlie & Delta", 2);
        Assert.That(teamA.Elo, Is.EqualTo(Configs.GetConfigData().Competitive.TeamStartingElo));

        var (deltaA, deltaB) = TeamEloEngine.ComputeMatchDeltas(teamA.Elo, teamB.Elo, 1.0);
        Assert.Multiple(() =>
        {
            Assert.That(deltaA, Is.GreaterThan(0));
            Assert.That(deltaB, Is.EqualTo(-deltaA));
        });

        var (updatedA, updatedB) = Queries.PersistCrMatch(
            seasonId, "de_mirage", DateTime.UtcNow,
            "1-2", "Alpha & Bravo", "3-4", "Charlie & Delta", 2,
            13, 7, "A", deltaA, deltaB);

        Assert.Multiple(() =>
        {
            Assert.That(updatedA.Elo, Is.EqualTo(teamA.Elo + deltaA));
            Assert.That(updatedA.MatchesWon, Is.EqualTo(1));
            Assert.That(updatedA.RoundsWon, Is.EqualTo(13));
            Assert.That(updatedB.Elo, Is.EqualTo(teamB.Elo + deltaB));
            Assert.That(updatedB.MatchesWon, Is.EqualTo(0));
            Assert.That(Queries.GetTopCrTeams(seasonId, 10), Has.Count.EqualTo(2));
        });

        // Cancelled matches record history but never touch ELO.
        var (afterCancelA, _) = Queries.PersistCrMatch(
            seasonId, "de_mirage", DateTime.UtcNow,
            "1-2", "Alpha & Bravo", "3-4", "Charlie & Delta", 2,
            3, 2, "cancelled", 99, -99);
        Assert.Multiple(() =>
        {
            Assert.That(afterCancelA.Elo, Is.EqualTo(updatedA.Elo));
            Assert.That(afterCancelA.MatchesPlayed, Is.EqualTo(1));
        });
    }
}
