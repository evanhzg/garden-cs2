using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;
using RetakesAllocatorCore.Managers;

namespace RetakesAllocatorTest;

public class ForceBuyAndEconomyTests : BaseTestFixture
{
    [TearDown]
    public void ResetRoundTypeManager()
    {
        RoundTypeManager.Instance.SetNextRoundTypeOverride(null);
        RoundTypeManager.Instance.SetCurrentRoundType(null);
        RoundTypeManager.Instance.SetForceBuyTeam(null);
        RoundTypeManager.Instance.ResetRoundLiveTime();
    }

    [Test]
    public void TestEffectiveRoundType()
    {
        RoundTypeManager.Instance.SetForceBuyTeam(CsTeam.Terrorist);

        Assert.Multiple(() =>
        {
            Assert.That(
                RoundTypeManager.Instance.GetEffectiveRoundType(RoundType.HalfBuy, CsTeam.Terrorist),
                Is.EqualTo(RoundType.HalfBuy));
            Assert.That(
                RoundTypeManager.Instance.GetEffectiveRoundType(RoundType.HalfBuy, CsTeam.CounterTerrorist),
                Is.EqualTo(RoundType.FullBuy));
            // Non-force-buy round types are unaffected
            Assert.That(
                RoundTypeManager.Instance.GetEffectiveRoundType(RoundType.Pistol, CsTeam.CounterTerrorist),
                Is.EqualTo(RoundType.Pistol));
            Assert.That(
                RoundTypeManager.Instance.GetEffectiveRoundType(RoundType.FullBuy, CsTeam.Terrorist),
                Is.EqualTo(RoundType.FullBuy));
        });

        RoundTypeManager.Instance.SetForceBuyTeam(null);
        Assert.That(
            RoundTypeManager.Instance.GetEffectiveRoundType(RoundType.HalfBuy, CsTeam.CounterTerrorist),
            Is.EqualTo(RoundType.HalfBuy));
    }

    [Test]
    public void TestForceBuyTeamIsChosenOnForceBuyRounds()
    {
        RoundTypeManager.Instance.SetNextRoundTypeOverride(RoundType.HalfBuy);
        OnRoundPostStartHelper.Handle(
            new List<int>(),
            i => (ulong) i,
            x => CsTeam.None,
            x => { },
            (x, items, slot) => { },
            x => false,
            out var roundType
        );
        Assert.That(roundType, Is.EqualTo(RoundType.HalfBuy));
        Assert.That(RoundTypeManager.Instance.ForceBuyTeam,
            Is.EqualTo(CsTeam.Terrorist).Or.EqualTo(CsTeam.CounterTerrorist));

        RoundTypeManager.Instance.SetNextRoundTypeOverride(RoundType.Pistol);
        OnRoundPostStartHelper.Handle(
            new List<int>(),
            i => (ulong) i,
            x => CsTeam.None,
            x => { },
            (x, items, slot) => { },
            x => false,
            out roundType
        );
        Assert.That(roundType, Is.EqualTo(RoundType.Pistol));
        Assert.That(RoundTypeManager.Instance.ForceBuyTeam, Is.Null);
    }

    [Test]
    public async Task TestNonDefaultPistolLosesKevlarOnPistolRound()
    {
        // Disable random allocation so unset preferences deterministically fall back to defaults
        Configs.OverrideConfigDataForTests(new ConfigData
        {
            AllowedWeaponSelectionTypes = new List<WeaponSelectionType>
                {WeaponSelectionType.PlayerChoice, WeaponSelectionType.Default},
        });

        // Player 1 prefers a Deagle on pistol rounds; player 2 keeps the default
        await Queries.SetWeaponPreferenceForUserAsync(1, CsTeam.Terrorist, WeaponAllocationType.PistolRound,
            CsItem.Deagle);

        RoundTypeManager.Instance.SetNextRoundTypeOverride(RoundType.Pistol);

        var itemsByPlayer = new Dictionary<int, ICollection<CsItem>>();
        OnRoundPostStartHelper.Handle(
            new List<int> {1, 2},
            i => (ulong) i,
            x => CsTeam.Terrorist,
            x => { },
            (player, items, slot) => { itemsByPlayer[player] = items; },
            x => false,
            out var roundType
        );

        Assert.That(roundType, Is.EqualTo(RoundType.Pistol));
        Assert.Multiple(() =>
        {
            // Non-default pistol -> no kevlar
            Assert.That(itemsByPlayer[1], Does.Contain(CsItem.Deagle));
            Assert.That(itemsByPlayer[1], Does.Not.Contain(CsItem.Kevlar));
            Assert.That(itemsByPlayer[1], Does.Not.Contain(CsItem.KevlarHelmet));

            // Default pistol -> kevlar as usual
            Assert.That(itemsByPlayer[2], Does.Contain(CsItem.Glock));
            Assert.That(itemsByPlayer[2], Does.Contain(CsItem.Kevlar));
        });
    }

    [Test]
    public async Task TestPistolRoundEconomyCanBeDisabled()
    {
        Configs.OverrideConfigDataForTests(new ConfigData
        {
            AllowedWeaponSelectionTypes = new List<WeaponSelectionType>
                {WeaponSelectionType.PlayerChoice, WeaponSelectionType.Default},
            EnablePistolRoundEconomy = false,
        });

        await Queries.SetWeaponPreferenceForUserAsync(1, CsTeam.Terrorist, WeaponAllocationType.PistolRound,
            CsItem.Deagle);

        RoundTypeManager.Instance.SetNextRoundTypeOverride(RoundType.Pistol);

        var itemsByPlayer = new Dictionary<int, ICollection<CsItem>>();
        OnRoundPostStartHelper.Handle(
            new List<int> {1},
            i => (ulong) i,
            x => CsTeam.Terrorist,
            x => { },
            (player, items, slot) => { itemsByPlayer[player] = items; },
            x => false,
            out _
        );

        Assert.That(itemsByPlayer[1], Does.Contain(CsItem.Kevlar));
    }

    [Test]
    public async Task TestZeusPreferenceIsPersistedAndAllocated()
    {
        Assert.That((await Queries.GetUserSettings(1))?.GetZeusPreference() ?? false, Is.False);

        await Queries.SetZeusPreferenceAsync(1, true);
        Assert.That((await Queries.GetUserSettings(1))!.GetZeusPreference(), Is.True);

        RoundTypeManager.Instance.SetNextRoundTypeOverride(RoundType.Pistol);
        var itemsByPlayer = new Dictionary<int, ICollection<CsItem>>();
        OnRoundPostStartHelper.Handle(
            new List<int> {1, 2},
            i => (ulong) i,
            x => CsTeam.Terrorist,
            x => { },
            (player, items, slot) => { itemsByPlayer[player] = items; },
            x => false,
            out _
        );

        Assert.Multiple(() =>
        {
            Assert.That(itemsByPlayer[1], Does.Contain(CsItem.Zeus));
            Assert.That(itemsByPlayer[2], Does.Not.Contain(CsItem.Zeus));
        });

        await Queries.SetZeusPreferenceAsync(1, false);
        Assert.That((await Queries.GetUserSettings(1))!.GetZeusPreference(), Is.False);
    }

    [Test]
    public void TestWeaponChangeWindow()
    {
        // Before the round goes live the window is always open
        RoundTypeManager.Instance.ResetRoundLiveTime();
        Assert.That(RoundTypeManager.Instance.IsInWeaponChangeWindow(0), Is.True);

        RoundTypeManager.Instance.SetRoundLive();
        Assert.That(RoundTypeManager.Instance.IsInWeaponChangeWindow(10), Is.True);

        Thread.Sleep(60);
        Assert.That(RoundTypeManager.Instance.IsInWeaponChangeWindow(0.01), Is.False);
    }

    [Test]
    public void TestIsWeaponAllocationAllowedUsesWindow()
    {
        Configs.OverrideConfigDataForTests(new ConfigData
        {
            AllowAllocationAfterFreezeTime = true,
            WeaponChangeWindowAfterRoundStartSeconds = 0.01,
        });

        // Freeze time always allows allocation
        Assert.That(WeaponHelpers.IsWeaponAllocationAllowed(true), Is.True);

        RoundTypeManager.Instance.SetRoundLive();
        Thread.Sleep(60);
        Assert.That(WeaponHelpers.IsWeaponAllocationAllowed(false), Is.False);

        Configs.GetConfigData().WeaponChangeWindowAfterRoundStartSeconds = 100;
        Assert.That(WeaponHelpers.IsWeaponAllocationAllowed(false), Is.True);
    }
}
