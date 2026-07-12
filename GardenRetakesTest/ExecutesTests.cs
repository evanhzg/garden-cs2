using GardenRetakes.Core.GameModes;
using NUnit.Framework;

namespace GardenRetakes.Test;

[TestFixture]
public class ExecutesTests
{
    private static ExecuteStore StoreWithPlayable(string name = "rush", string site = "A")
    {
        var store = new ExecuteStore();
        store.TryAdd(name, site, "Evan", out _);
        var strategy = store.Find(name)!;
        strategy.TStarts.Add(new ExecutePosition { X = 1, Y = 2, Z = 3, Yaw = 90 });
        strategy.CtSetups.Add(new ExecutePosition { X = 9, Y = 8, Z = 7 });
        strategy.Utilities.Add(new UtilityThrow
        {
            Type = UtilityType.Smoke, X = 1, Y = 1, Z = 1, VelX = 100, VelY = 0, VelZ = 300, DelaySeconds = 0.5f,
        });
        return store;
    }

    [Test]
    public void AddValidatesNameAndSite()
    {
        var store = new ExecuteStore();
        Assert.That(store.TryAdd("rush", "a", null, out _), Is.True);
        Assert.That(store.Find("RUSH"), Is.Not.Null, "lookup is case-insensitive");

        Assert.That(store.TryAdd("rush", "B", null, out var error), Is.False);
        Assert.That(error, Is.EqualTo("duplicate"));

        // R11: multi-word names are allowed now; empty/too-long are not.
        Assert.That(store.TryAdd("A Site VS Long", "A", null, out _), Is.True);
        Assert.That(store.Find("a site vs long"), Is.Not.Null);
        Assert.That(store.TryAdd("   ", "A", null, out error), Is.False);
        Assert.That(error, Is.EqualTo("bad_name"));

        Assert.That(store.TryAdd("mid", "C", null, out error), Is.False);
        Assert.That(error, Is.EqualTo("bad_site"));
    }

    [Test]
    public void OnlyPlayableStrategiesArePicked()
    {
        var store = StoreWithPlayable("rush", "A");
        store.TryAdd("empty", "B", null, out _); // no positions -> not playable

        Assert.That(store.Playable, Has.Count.EqualTo(1));
        Assert.That(store.PickRandom(new Random(1))!.Name, Is.EqualTo("rush"));
        Assert.That(store.PickRandom(new Random(1), "B"), Is.Null);
        Assert.That(store.PickRandom(new Random(1), "a")!.Name, Is.EqualTo("rush"));
    }

    [Test]
    public void WeightedPickRespectsZeroAndBias()
    {
        var store = StoreWithPlayable("rush", "A");
        store.TryAdd("mid", "A", null, out _);
        var mid = store.Find("mid")!;
        mid.TStarts.Add(new ExecutePosition());
        mid.CtSetups.Add(new ExecutePosition());
        mid.Weight = 0; // never picked randomly

        var random = new Random(42);
        for (var i = 0; i < 50; i++)
        {
            Assert.That(store.PickRandom(random)!.Name, Is.EqualTo("rush"));
        }

        // Weight 0 on everything -> nothing pickable.
        store.Find("rush")!.Weight = 0;
        Assert.That(store.PickRandom(random), Is.Null);
    }

    [Test]
    public void UtilityTeamRoundTripsAndDefaultsToT()
    {
        var store = StoreWithPlayable();
        store.Find("rush")!.Utilities[0].Team = "CT";

        var restored = new ExecuteStore();
        restored.Load(store.Serialize());
        Assert.That(restored.Find("rush")!.Utilities[0].Team, Is.EqualTo("CT"));

        // Old JSONs without a Team field default to T.
        Assert.That(new UtilityThrow().Team, Is.EqualTo("T"));
    }

    [Test]
    public void SerializationRoundTrips()
    {
        var store = StoreWithPlayable();
        var json = store.Serialize();

        var restored = new ExecuteStore();
        restored.Load(json);

        var strategy = restored.Find("rush")!;
        Assert.That(strategy.Site, Is.EqualTo("A"));
        Assert.That(strategy.AddedBy, Is.EqualTo("Evan"));
        Assert.That(strategy.TStarts, Has.Count.EqualTo(1));
        Assert.That(strategy.TStarts[0].Yaw, Is.EqualTo(90));
        Assert.That(strategy.Utilities[0].Type, Is.EqualTo(UtilityType.Smoke));
        Assert.That(strategy.Utilities[0].VelZ, Is.EqualTo(300));
        Assert.That(strategy.IsPlayable, Is.True);
    }

    [Test]
    public void RemoveWorksByNameCaseInsensitive()
    {
        var store = StoreWithPlayable();
        Assert.That(store.Remove("RUSH"), Is.True);
        Assert.That(store.Strategies, Is.Empty);
        Assert.That(store.Remove("rush"), Is.False);
    }
}
