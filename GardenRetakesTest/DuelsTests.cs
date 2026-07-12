using GardenRetakes.Core.GameModes;
using NUnit.Framework;

namespace GardenRetakes.Test;

[TestFixture]
public class DuelsTests
{
    [Test]
    public void PairsClosestSpawnsIntoArenas()
    {
        var spawns = new List<DuelArenas.ArenaSpawn>
        {
            new(1, 0, 0, 0),
            new(2, 100, 0, 0),     // pairs with 1 (100u)
            new(3, 5000, 0, 0),
            new(4, 5200, 0, 0),    // pairs with 3 (200u)
            new(5, 20000, 0, 0),   // nobody in range
        };

        var arenas = DuelArenas.BuildArenas(spawns, maxPairDistance: 1000);

        Assert.That(arenas, Has.Count.EqualTo(2));
        Assert.That(arenas[0], Is.EqualTo(new DuelArenas.Arena(1, 2)), "closest pair first");
        Assert.That(arenas[1], Is.EqualTo(new DuelArenas.Arena(3, 4)));
    }

    [Test]
    public void FirstTwoPlayersFightRestQueues()
    {
        var session = new DuelSession();
        Assert.That(session.AddPlayer(1), Is.True);
        Assert.That(session.AddPlayer(2), Is.True);
        Assert.That(session.AddPlayer(3), Is.False);
        Assert.That(session.HasActiveDuel, Is.True);
        Assert.That(session.Queue, Is.EqualTo(new ulong[] {3}));
    }

    [Test]
    public void DeathRotatesLoserBehindQueue()
    {
        var session = new DuelSession();
        session.AddPlayer(1);
        session.AddPlayer(2);
        session.AddPlayer(3);

        var outcome = session.OnFighterDeath(2)!;
        Assert.That(outcome.WinnerId, Is.EqualTo(1));
        Assert.That(outcome.LoserId, Is.EqualTo(2));
        Assert.That(outcome.NextA, Is.EqualTo(1));
        Assert.That(outcome.NextB, Is.EqualTo(3));
        Assert.That(outcome.LoserRotatedOut, Is.True);
        Assert.That(session.Queue, Is.EqualTo(new ulong[] {2}));
        Assert.That(session.Wins[1], Is.EqualTo(1));
    }

    [Test]
    public void TwoPlayersKeepFightingWithoutQueue()
    {
        var session = new DuelSession();
        session.AddPlayer(1);
        session.AddPlayer(2);

        var outcome = session.OnFighterDeath(1)!;
        Assert.That(outcome.LoserRotatedOut, Is.False);
        Assert.That(session.HasActiveDuel, Is.True);
        Assert.That(session.IsFighter(1), Is.True);
        Assert.That(session.IsFighter(2), Is.True);
    }

    [Test]
    public void DisconnectPromotesFromQueue()
    {
        var session = new DuelSession();
        session.AddPlayer(1);
        session.AddPlayer(2);
        session.AddPlayer(3);

        var promoted = session.RemovePlayer(2);
        Assert.That(promoted, Is.EqualTo(3));
        Assert.That(session.HasActiveDuel, Is.True);

        var nobody = session.RemovePlayer(999);
        Assert.That(nobody, Is.Null);
    }

    [Test]
    public void NonFighterDeathIsIgnored()
    {
        var session = new DuelSession();
        session.AddPlayer(1);
        session.AddPlayer(2);
        session.AddPlayer(3);
        Assert.That(session.OnFighterDeath(3), Is.Null);
    }
}
