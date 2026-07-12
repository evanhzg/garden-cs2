using GardenRetakes.Core.GameModes;
using NUnit.Framework;

namespace GardenRetakes.Test;

[TestFixture]
public class DuelsV2Tests
{
    // ---------- DuelArenaStore ----------

    [Test]
    public void ArenaStoreValidatesAndRoundTrips()
    {
        var store = new DuelArenaStore();
        Assert.That(store.TryAdd("mid", "Evan", out _), Is.True);
        Assert.That(store.TryAdd("mid", null, out var error), Is.False);
        Assert.That(error, Is.EqualTo("duplicate"));
        // R11: multi-word names allowed; empty is not.
        Assert.That(store.TryAdd("A Site VS Long", "Evan", out _), Is.True);
        Assert.That(store.Find("a site vs long"), Is.Not.Null);
        Assert.That(store.TryAdd("  ", null, out error), Is.False);
        Assert.That(error, Is.EqualTo("bad_name"));

        var arena = store.Find("MID")!;
        Assert.That(arena.IsComplete, Is.False);
        arena.EndA = new ExecutePosition { X = 1 };
        arena.EndB = new ExecutePosition { X = 2 };
        Assert.That(arena.IsComplete, Is.True);

        var restored = new DuelArenaStore();
        restored.Load(store.Serialize());
        Assert.That(restored.Complete, Has.Count.EqualTo(1));
        Assert.That(restored.Find("mid")!.AddedBy, Is.EqualTo("Evan"));
    }

    // ---------- DuelManager: parallel lanes ----------

    [Test]
    public void FourPlayersFillTwoParallelLanes()
    {
        var manager = new DuelManager { MaxLanes = 2 };
        manager.AddPlayer(1);
        manager.AddPlayer(2);
        manager.AddPlayer(3);
        manager.AddPlayer(4);

        Assert.That(manager.Lanes, Has.Count.EqualTo(2));
        Assert.That(manager.Lanes.All(l => l.IsReady), Is.True);
        Assert.That(manager.Queue, Is.Empty);
    }

    [Test]
    public void FifthPlayerQueuesAndRotatesInOnDeath()
    {
        var manager = new DuelManager { MaxLanes = 2 };
        for (ulong id = 1; id <= 5; id++) manager.AddPlayer(id);

        Assert.That(manager.Queue, Is.EqualTo(new ulong[] {5}));

        var result = manager.OnDeath(2)!;
        Assert.That(result.WinnerId, Is.EqualTo(1));
        Assert.That(result.LoserRotatedOut, Is.True);
        Assert.That(result.Lane.Contains(5), Is.True, "queued player steps in");
        Assert.That(manager.Queue, Is.EqualTo(new ulong[] {2}));
        Assert.That(manager.Wins[1], Is.EqualTo(1));
    }

    [Test]
    public void MaxLanesRespectsArenaCap()
    {
        var manager = new DuelManager { MaxLanes = 1 };
        for (ulong id = 1; id <= 4; id++) manager.AddPlayer(id);
        Assert.That(manager.Lanes, Has.Count.EqualTo(1));
        Assert.That(manager.Queue, Has.Count.EqualTo(2));
    }

    [Test]
    public void DisconnectReslotsPartner()
    {
        var manager = new DuelManager { MaxLanes = 2 };
        for (ulong id = 1; id <= 5; id++) manager.AddPlayer(id);

        manager.RemovePlayer(1); // partner 2 must be re-slotted with queued 5
        Assert.That(manager.IsKnown(1), Is.False);
        Assert.That(manager.LaneOf(2), Is.Not.Null);
        Assert.That(manager.Lanes.All(l => l.IsReady), Is.True);
        Assert.That(manager.Queue, Is.Empty);
    }

    // ---------- DuelManager: challenges ----------

    [Test]
    public void ChallengeLaneIgnoresQueueAndTracksScore()
    {
        var manager = new DuelManager { MaxLanes = 2 };
        for (ulong id = 1; id <= 5; id++) manager.AddPlayer(id);

        var lane = manager.StartChallenge(1, 3, firstTo: 2);
        Assert.That(lane.IsChallenge, Is.True);
        Assert.That(manager.LaneOf(1), Is.EqualTo(lane));

        // 1 kills 3 -> 1-0, not finished, no rotation despite the queue.
        var r1 = manager.OnDeath(3)!;
        Assert.That(r1.ChallengeFinished, Is.False);
        Assert.That(r1.LoserRotatedOut, Is.False);
        Assert.That(lane.ScoreLine, Is.EqualTo("1-0"));
        Assert.That(lane.Contains(3), Is.True, "loser stays in a challenge");

        // 1 kills 3 again -> 2-0, first-to-2 reached -> finished + dissolved.
        var r2 = manager.OnDeath(3)!;
        Assert.That(r2.ChallengeFinished, Is.True);
        Assert.That(r2.WinnerId, Is.EqualTo(1));
        Assert.That(manager.LaneOf(1)?.IsChallenge ?? false, Is.False, "players rejoin the normal pool");
    }

    [Test]
    public void InfiniteChallengeRunsUntilCancelled()
    {
        var manager = new DuelManager { MaxLanes = 2 };
        manager.AddPlayer(1);
        manager.AddPlayer(2);

        var lane = manager.StartChallenge(1, 2, firstTo: null);
        for (var i = 0; i < 5; i++)
        {
            Assert.That(manager.OnDeath(2)!.ChallengeFinished, Is.False);
        }

        Assert.That(lane.ScoreA + lane.ScoreB, Is.EqualTo(5));
        Assert.That(manager.CancelChallenge(2), Is.True);
        Assert.That(manager.Lanes.Any(l => l.IsChallenge), Is.False);
    }

    [Test]
    public void ChallengeExtractionReslotsAbandonedPartners()
    {
        var manager = new DuelManager { MaxLanes = 2 };
        for (ulong id = 1; id <= 4; id++) manager.AddPlayer(id);

        // 1 (lane with 2) challenges 3 (lane with 4): 2 and 4 should pair up.
        manager.StartChallenge(1, 3, 5);
        Assert.That(manager.LaneOf(2), Is.Not.Null);
        Assert.That(manager.LaneOf(2)!.Contains(4), Is.True);
    }
}
