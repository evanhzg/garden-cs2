using GardenRetakes.Core.GameModes;
using NUnit.Framework;

namespace GardenRetakes.Test;

[TestFixture]
public class GameModeManagerTests
{
    [Test]
    public void DefaultsToRetakes()
    {
        var manager = new GameModeManager();
        Assert.That(manager.CurrentMode, Is.EqualTo(GameModeKind.Retakes));
    }

    [Test]
    public void ChangesModeAndFiresEvent()
    {
        var manager = new GameModeManager();
        (GameModeKind from, GameModeKind to)? fired = null;
        manager.ModeChanged += (from, to) => fired = (from, to);

        Assert.That(manager.TryChangeMode(GameModeKind.Duels, out var error), Is.True);
        Assert.That(error, Is.Null);
        Assert.That(manager.CurrentMode, Is.EqualTo(GameModeKind.Duels));
        Assert.That(fired, Is.EqualTo((GameModeKind.Retakes, GameModeKind.Duels)));
    }

    [Test]
    public void RejectsChangeToSameMode()
    {
        var manager = new GameModeManager();
        Assert.That(manager.TryChangeMode(GameModeKind.Retakes, out var error), Is.False);
        Assert.That(error, Is.EqualTo("already_active"));
    }

    [Test]
    public void BlocksChangeDuringMatch()
    {
        var manager = new GameModeManager { IsMatchInProgress = true };
        Assert.That(manager.TryChangeMode(GameModeKind.Executes, out var error), Is.False);
        Assert.That(error, Is.EqualTo("match_in_progress"));
        Assert.That(manager.CurrentMode, Is.EqualTo(GameModeKind.Retakes));
    }

    [TestCase("duels", GameModeKind.Duels)]
    [TestCase("1v1", GameModeKind.Duels)]
    [TestCase("EXEC", GameModeKind.Executes)]
    [TestCase("fast-strat", GameModeKind.FastStrat)]
    [TestCase("retakes", GameModeKind.Retakes)]
    [TestCase("edit", GameModeKind.Edit)]
    [TestCase("EDITOR", GameModeKind.Edit)]
    public void ParsesModeAliases(string input, GameModeKind expected)
    {
        Assert.That(GameModeManager.TryParseMode(input, out var mode), Is.True);
        Assert.That(mode, Is.EqualTo(expected));
    }

    [Test]
    public void RejectsUnknownModeName()
    {
        Assert.That(GameModeManager.TryParseMode("bhop", out _), Is.False);
    }

    [Test]
    public void SmallServerAutoActivatesByHumanCount()
    {
        var manager = new GameModeManager { SmallServerMaxHumans = 3 };
        Assert.That(manager.IsSmallServerActive(0), Is.False);
        Assert.That(manager.IsSmallServerActive(2), Is.True);
        Assert.That(manager.IsSmallServerActive(3), Is.True);
        Assert.That(manager.IsSmallServerActive(4), Is.False);
    }

    [Test]
    public void SmallServerForcedStatesWin()
    {
        var manager = new GameModeManager { SmallServerMaxHumans = 3 };

        manager.SetSmallServerState(SmallServerState.ForcedOn);
        Assert.That(manager.IsSmallServerActive(9), Is.True);

        manager.SetSmallServerState(SmallServerState.ForcedOff);
        Assert.That(manager.IsSmallServerActive(2), Is.False);
    }

    [Test]
    public void SmallServerOnlyAppliesToRetakesMode()
    {
        var manager = new GameModeManager { SmallServerMaxHumans = 3 };
        manager.SetSmallServerState(SmallServerState.ForcedOn);
        manager.TryChangeMode(GameModeKind.Duels, out _);
        Assert.That(manager.IsSmallServerActive(2), Is.False);
    }
}
