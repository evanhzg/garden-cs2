using System.Collections;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;

namespace RetakesAllocatorTest;

public class NadeAllocationTests : BaseTestFixture
{
    [Test]
    public void TestGetUtilForTeam()
    {
        var util = NadeHelpers.GetUtilForTeam("de_mirage", RoundType.Pistol, CsTeam.Terrorist, 4);
        Assert.That(util.Count, Is.EqualTo(4));

        util = NadeHelpers.GetUtilForTeam(null, RoundType.Pistol, CsTeam.CounterTerrorist, 0);
        Assert.That(util.Count, Is.EqualTo(0));
    }

    [Test]
    [TestCase(RoundType.Pistol)]
    [TestCase(RoundType.HalfBuy)]
    [TestCase(RoundType.FullBuy)]
    public void TestTerroristUtilIsRestricted(RoundType roundType)
    {
        // Ts (defending the bomb) never get molotovs and get at most one smoke
        // for the whole team, on every round type.
        for (var i = 0; i < 20; i++)
        {
            var util = NadeHelpers.GetUtilForTeam(null, roundType, CsTeam.Terrorist, 10);
            Assert.That(util, Does.Not.Contain(CsItem.Molotov));
            Assert.That(util, Does.Not.Contain(CsItem.Incendiary));
            Assert.That(util.Count(n => n == CsItem.Smoke), Is.LessThanOrEqualTo(1));
        }
    }

    [Test]
    public void TestForceBuyTeamUtilIsRestricted()
    {
        // The force-buying team (even CT) gets the reduced utility pool.
        for (var i = 0; i < 20; i++)
        {
            var util = NadeHelpers.GetUtilForTeam(null, RoundType.HalfBuy, CsTeam.CounterTerrorist, 10,
                isForceBuyTeam: true);
            Assert.That(util, Does.Not.Contain(CsItem.Incendiary));
            Assert.That(util, Does.Not.Contain(CsItem.Molotov));
            Assert.That(util.Count(n => n == CsItem.Smoke), Is.LessThanOrEqualTo(1));
        }
    }

    [Test]
    public void TestUtilityDistributionIsConfigurable()
    {
        // Disable the restriction and make the pool molly-only.
        Configs.OverrideConfigDataForTests(new ConfigData
        {
            RestrictedUtility = new RestrictedUtilityConfig {Enabled = false},
            NadeDistributionWeights = new() {{CsItem.Molotov, 1}},
        });

        var tUtil = NadeHelpers.GetUtilForTeam(null, RoundType.FullBuy, CsTeam.Terrorist, 4);
        Assert.That(tUtil, Is.Not.Empty);
        Assert.That(tUtil, Is.All.EqualTo(CsItem.Molotov));

        // The same Molotov weight entry yields incendiaries on the CT side.
        var ctUtil = NadeHelpers.GetUtilForTeam(null, RoundType.FullBuy, CsTeam.CounterTerrorist, 4);
        Assert.That(ctUtil, Is.Not.Empty);
        Assert.That(ctUtil, Is.All.EqualTo(CsItem.Incendiary));
    }

    [Test]
    public void TestMaxSmokesPerTeamIsRespected()
    {
        Configs.OverrideConfigDataForTests(new ConfigData
        {
            RestrictedUtility = new RestrictedUtilityConfig {Enabled = false},
            NadeDistributionWeights = new() {{CsItem.Smoke, 1}},
            MaxNades = new()
            {
                {
                    NadeHelpers.GlobalSettingName, new()
                    {
                        {CsTeam.Terrorist, new() {{CsItem.Smoke, 2}}},
                        {CsTeam.CounterTerrorist, new() {{CsItem.Smoke, 2}}},
                    }
                }
            },
        });

        var util = NadeHelpers.GetUtilForTeam(null, RoundType.FullBuy, CsTeam.Terrorist, 10);
        Assert.Multiple(() =>
        {
            Assert.That(util, Has.Count.EqualTo(2));
            Assert.That(util, Is.All.EqualTo(CsItem.Smoke));
        });
    }

    [Test]
    public void TestRestrictedOverrideCapsAreConfigurable()
    {
        // Allow restricted teams two smokes instead of the default single one.
        var config = new ConfigData();
        config.RestrictedUtility.MaxTeamNadesOverride[CsItem.Smoke] = 2;
        config.NadeDistributionWeights = new() {{CsItem.Smoke, 1}};
        Configs.OverrideConfigDataForTests(config);

        var util = NadeHelpers.GetUtilForTeam(null, RoundType.FullBuy, CsTeam.Terrorist, 10);
        // Still limited by the global MaxNades default for T smokes (1); raise it too.
        config.MaxNades[NadeHelpers.GlobalSettingName][CsTeam.Terrorist][CsItem.Smoke] = 5;
        util = NadeHelpers.GetUtilForTeam(null, RoundType.FullBuy, CsTeam.Terrorist, 10);
        Assert.That(util.Count(n => n == CsItem.Smoke), Is.EqualTo(2));
    }

    [Test]
    public void TestPerPlayerAllowanceIsConfigurable()
    {
        // Default allowance: one smoke per player.
        var nades = new Stack<CsItem>(new[] {CsItem.Smoke, CsItem.Smoke, CsItem.Smoke});
        var nadesByPlayer = new Dictionary<int, ICollection<CsItem>>();
        NadeHelpers.AllocateNadesToPlayers(nades, new List<int> {1}, nadesByPlayer);
        Assert.That(nadesByPlayer[1], Has.Count.EqualTo(1));

        // Raised allowance: the same player can carry all three.
        Configs.OverrideConfigDataForTests(new ConfigData
        {
            MaxNadesPerPlayer = new() {{CsItem.Smoke, 3}},
        });
        nades = new Stack<CsItem>(new[] {CsItem.Smoke, CsItem.Smoke, CsItem.Smoke});
        nadesByPlayer = new Dictionary<int, ICollection<CsItem>>();
        NadeHelpers.AllocateNadesToPlayers(nades, new List<int> {1}, nadesByPlayer);
        Assert.That(nadesByPlayer[1], Has.Count.EqualTo(3));
    }

    [Test]
    public void TestAllocateNadesToPlayers()
    {
        var util = NadeHelpers.GetUtilForTeam(null, RoundType.Pistol, CsTeam.Terrorist, 4);
        Dictionary<int, ICollection<CsItem>> nadesByPlayer = new();
        NadeHelpers.AllocateNadesToPlayers(new Stack<CsItem>(util), new List<int> {1, 2, 3, 4}, nadesByPlayer);
        Assert.That(util, Is.EquivalentTo(nadesByPlayer.Values.SelectMany(x => x)));
        
        util = NadeHelpers.GetUtilForTeam("de_dust2", RoundType.Pistol, CsTeam.CounterTerrorist, 0);
        nadesByPlayer = new();
        NadeHelpers.AllocateNadesToPlayers(new Stack<CsItem>(util), new List<int>(), nadesByPlayer);
        Assert.That(util, Is.EquivalentTo(nadesByPlayer.Values.SelectMany(x => x)));
    }
}