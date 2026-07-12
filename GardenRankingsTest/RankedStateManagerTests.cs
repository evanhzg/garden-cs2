using GardenRankingsCore.Config;
using GardenRankingsCore.Managers;

namespace GardenRankingsTest;

public class RankedStateManagerTests : BaseTestFixture
{
    private static readonly List<ulong> FourPlayers = new() {1, 2, 3, 4};
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void FullStartVoteFlow()
    {
        var manager = new RankedStateManager();

        Assert.That(manager.TryStartVote(1, FourPlayers, T0), Is.EqualTo(StartVoteOutcome.VoteStarted));
        Assert.That(manager.State, Is.EqualTo(RankedState.StartVoteInProgress));

        // Initiator auto-accepted; remaining three must accept when ratio is 1.0.
        Assert.That(manager.CastVote(2, true, T0), Is.EqualTo(VoteCastResult.Registered));
        Assert.That(manager.CastVote(3, true, T0), Is.EqualTo(VoteCastResult.Registered));
        Assert.That(manager.CastVote(4, true, T0), Is.EqualTo(VoteCastResult.Passed));

        Assert.That(manager.IsActive, Is.True);
    }

    [Test]
    public void SingleDeclineFailsUnanimousVote()
    {
        var manager = new RankedStateManager();
        manager.TryStartVote(1, FourPlayers, T0);

        Assert.That(manager.CastVote(2, false, T0), Is.EqualTo(VoteCastResult.Failed));
        Assert.That(manager.State, Is.EqualTo(RankedState.Inactive));
    }

    [Test]
    public void NonUnanimousRatioAllowsSomeDeclines()
    {
        Configs.GetConfigData().Ranked.VoteRequiredRatio = 0.5;

        var manager = new RankedStateManager();
        manager.TryStartVote(1, FourPlayers, T0);

        // Need ceil(0.5 * 4) = 2 accepts; initiator + one more.
        Assert.That(manager.CastVote(2, true, T0), Is.EqualTo(VoteCastResult.Passed));
        Assert.That(manager.IsActive, Is.True);
    }

    [Test]
    public void NotEnoughPlayersBlocksVote()
    {
        var manager = new RankedStateManager();
        Assert.That(
            manager.TryStartVote(1, new List<ulong> {1, 2, 3}, T0),
            Is.EqualTo(StartVoteOutcome.NotEnoughPlayers));
    }

    [Test]
    public void VoteTimeoutFailsByDefault()
    {
        var manager = new RankedStateManager();
        manager.TryStartVote(1, FourPlayers, T0);

        Assert.That(manager.Tick(T0.AddSeconds(5)), Is.EqualTo(TickOutcome.None));
        Assert.That(manager.Tick(T0.AddSeconds(60)), Is.EqualTo(TickOutcome.StartVoteFailed));
        Assert.That(manager.State, Is.EqualTo(RankedState.Inactive));
    }

    [Test]
    public void VoteTimeoutPassesWhenNonVotersCountAsAccept()
    {
        Configs.GetConfigData().Ranked.CountNonVotersAsAccept = true;

        var manager = new RankedStateManager();
        manager.TryStartVote(1, FourPlayers, T0);

        Assert.That(manager.Tick(T0.AddSeconds(60)), Is.EqualTo(TickOutcome.StartVotePassed));
        Assert.That(manager.IsActive, Is.True);
    }

    [Test]
    public void StopConfirmFlow()
    {
        var manager = new RankedStateManager();
        ActivateRanked(manager);

        Assert.That(
            manager.RequestStop(2, FourPlayers, T0),
            Is.EqualTo(StopRequestOutcome.ConfirmationPending));
        Assert.That(manager.IsActive, Is.True);

        // Someone else cannot confirm.
        Assert.That(manager.ConfirmStop(3, T0.AddSeconds(2)), Is.False);

        // Initiator confirms in time.
        Assert.That(manager.ConfirmStop(2, T0.AddSeconds(5)), Is.True);
        Assert.That(manager.State, Is.EqualTo(RankedState.Inactive));
    }

    [Test]
    public void StopConfirmExpires()
    {
        var manager = new RankedStateManager();
        ActivateRanked(manager);
        manager.RequestStop(2, FourPlayers, T0);

        Assert.That(manager.Tick(T0.AddSeconds(60)), Is.EqualTo(TickOutcome.StopConfirmExpired));
        Assert.That(manager.State, Is.EqualTo(RankedState.Active));

        // Too late to confirm now.
        Assert.That(manager.ConfirmStop(2, T0.AddSeconds(61)), Is.False);
    }

    [Test]
    public void StopVoteFlowWhenConfigured()
    {
        Configs.GetConfigData().Ranked.StopRequiresVote = true;

        var manager = new RankedStateManager();
        ActivateRanked(manager);

        Assert.That(
            manager.RequestStop(1, FourPlayers, T0),
            Is.EqualTo(StopRequestOutcome.StopVoteStarted));

        manager.CastVote(2, true, T0);
        manager.CastVote(3, true, T0);
        Assert.That(manager.CastVote(4, true, T0), Is.EqualTo(VoteCastResult.Passed));
        Assert.That(manager.State, Is.EqualTo(RankedState.Inactive));
    }

    [Test]
    public void PlayerCountDropStopsRanked()
    {
        var manager = new RankedStateManager();
        ActivateRanked(manager);

        Assert.That(manager.OnEligiblePlayerCountChanged(4), Is.False);
        Assert.That(manager.OnEligiblePlayerCountChanged(3), Is.True);
        Assert.That(manager.State, Is.EqualTo(RankedState.Inactive));
    }

    private static void ActivateRanked(RankedStateManager manager)
    {
        manager.TryStartVote(1, FourPlayers, T0);
        manager.CastVote(2, true, T0);
        manager.CastVote(3, true, T0);
        manager.CastVote(4, true, T0);
        Assert.That(manager.IsActive, Is.True);
    }
}
