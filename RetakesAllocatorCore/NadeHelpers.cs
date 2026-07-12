using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore.Config;

namespace RetakesAllocatorCore;

public enum MaxTeamNadesSetting
{
    None,
    One,
    Two,
    Three,
    Four,
    Five,
    Six,
    Seven,
    Eight,
    Nine,
    Ten,
    AveragePointFivePerPlayer,
    AverageOnePerPlayer,
    AverageOnePointFivePerPlayer,
    AverageTwoPerPlayer,
}

public class NadeHelpers
{
    public static string GlobalSettingName = "GLOBAL";

    /// <summary>
    /// Garden (ROADMAP R5): hard cap on a team's total nades per round, set by the
    /// small-server overlay while active (null = no override). Never persisted.
    /// </summary>
    public static int? GardenMaxTotalNadesOverride { get; set; }

    public static Stack<CsItem> GetUtilForTeam(string? map, RoundType roundType, CsTeam team, int numPlayers,
        bool isForceBuyTeam = false)
    {
        map ??= GlobalSettingName;

        var maxNadesSetting = GetMaxTeamNades(map, team, roundType);
        if (maxNadesSetting == MaxTeamNadesSetting.None)
        {
            return new();
        }

        var multiplier = maxNadesSetting switch
        {
            MaxTeamNadesSetting.AveragePointFivePerPlayer => 0.5,
            MaxTeamNadesSetting.AverageOnePerPlayer => 1,
            MaxTeamNadesSetting.AverageOnePointFivePerPlayer => 1.5,
            MaxTeamNadesSetting.AverageTwoPerPlayer => 2,
            _ => 0,
        };

        var maxTotalNades = maxNadesSetting switch
        {
            MaxTeamNadesSetting.One => 1,
            MaxTeamNadesSetting.Two => 2,
            MaxTeamNadesSetting.Three => 3,
            MaxTeamNadesSetting.Four => 4,
            MaxTeamNadesSetting.Five => 5,
            MaxTeamNadesSetting.Six => 6,
            MaxTeamNadesSetting.Seven => 7,
            MaxTeamNadesSetting.Eight => 8,
            MaxTeamNadesSetting.Nine => 9,
            MaxTeamNadesSetting.Ten => 10,
            _ => (int) Math.Ceiling(numPlayers * multiplier)
        };

        // Garden (R5): small-server overlay reduces the utility pool.
        if (GardenMaxTotalNadesOverride is { } gardenCap)
        {
            maxTotalNades = Math.Min(maxTotalNades, gardenCap);
        }

        Log.Debug($"Nade setting: {maxNadesSetting}. Total: {maxTotalNades}. Map: {map}");

        var molly = team == CsTeam.Terrorist ? CsItem.Molotov : CsItem.Incendiary;
        var config = Configs.GetConfigData();
        var restricted = config.RestrictedUtility;

        // Ts (defending the bomb) and the force-buying team of a force-buy round can
        // get a reduced pool; which nades and which extra caps apply is configurable.
        var isRestricted = restricted.Enabled &&
                           ((team == CsTeam.Terrorist && restricted.ApplyToTerrorists) ||
                            (isForceBuyTeam && restricted.ApplyToForceBuyTeam));

        // Config entries may use Molotov or Incendiary interchangeably; normalize to
        // this team's molly type so comparisons work for both sides.
        CsItem NormalizeForTeam(CsItem item) =>
            item is CsItem.Molotov or CsItem.Incendiary ? molly : item;

        bool IsAllowed(CsItem item) =>
            !isRestricted ||
            restricted.AllowedNades.Any(allowed => NormalizeForTeam(allowed) == NormalizeForTeam(item));

        // Weighted random distribution, from config.
        var nadeDistribution = new List<CsItem>();
        foreach (var (configItem, weight) in config.NadeDistributionWeights)
        {
            var item = NormalizeForTeam(configItem);
            if (weight <= 0 || !IsAllowed(item))
            {
                continue;
            }

            for (var i = 0; i < weight; i++)
            {
                nadeDistribution.Add(item);
            }
        }

        if (nadeDistribution.Count == 0)
        {
            return new();
        }

        // Team-wide caps: MaxNades (per map/team) plus any restricted-pool overrides.
        var nadeAllocations = new Dictionary<CsItem, int>();
        foreach (var item in nadeDistribution.Distinct().ToList())
        {
            var cap = GetMaxNades(map, team, item);
            if (isRestricted)
            {
                foreach (var (overrideItem, overrideCap) in restricted.MaxTeamNadesOverride)
                {
                    if (NormalizeForTeam(overrideItem) == item)
                    {
                        cap = Math.Min(cap, overrideCap);
                    }
                }
            }

            nadeAllocations[item] = cap;
        }

        var nades = new Stack<CsItem>();
        while (true)
        {
            if (nadeAllocations.Count == 0 || maxTotalNades <= 0)
            {
                break;
            }

            var nextNade = Utils.Choice(nadeDistribution);
            if (nadeAllocations[nextNade] <= 0)
            {
                nadeDistribution.RemoveAll(item => item == nextNade);
                nadeAllocations.Remove(nextNade);
                continue;
            }

            nades.Push(nextNade);
            nadeAllocations[nextNade]--;
            maxTotalNades--;
        }

        return nades;
    }

    private static MaxTeamNadesSetting GetMaxTeamNades(string map, CsTeam team, RoundType roundType)
    {
        if (Configs.GetConfigData().MaxTeamNades.TryGetValue(map, out var mapMaxNades))
        {
            if (mapMaxNades.TryGetValue(team, out var teamMaxNades))
            {
                if (teamMaxNades.TryGetValue(roundType, out var maxNadesSetting))
                {
                    return maxNadesSetting;
                }
            }
        }

        if (map == GlobalSettingName)
        {
            return MaxTeamNadesSetting.None;
        }

        return GetMaxTeamNades(GlobalSettingName, team, roundType);
    }

    private static int GetMaxNades(string map, CsTeam team, CsItem nade)
    {
        if (Configs.GetConfigData().MaxNades.TryGetValue(map, out var mapNades))
        {
            if (mapNades.TryGetValue(team, out var teamNades))
            {
                int nadeCount;
                if (teamNades.TryGetValue(nade, out nadeCount))
                {
                    return nadeCount;
                }

                if (nade is CsItem.Molotov or CsItem.Incendiary)
                {
                    var otherNade = nade == CsItem.Molotov ? CsItem.Incendiary : CsItem.Molotov;
                    if (teamNades.TryGetValue(otherNade, out nadeCount))
                    {
                        return nadeCount;
                    }
                }
            }
        }

        if (map == GlobalSettingName)
        {
            return 999999;
        }

        return GetMaxNades(GlobalSettingName, team, nade);
    }

    /// <summary>
    /// Whether a player already carrying these nades may receive one more of the
    /// given type, according to the configured MaxNadesPerPlayer. Types missing
    /// from the config are never handed out.
    /// </summary>
    private static bool CanAcceptNade(ICollection<CsItem> nades, CsItem nade)
    {
        var allowancePerType = Configs.GetConfigData().MaxNadesPerPlayer;

        if (!allowancePerType.TryGetValue(nade, out var max))
        {
            // Molotov and Incendiary configs are interchangeable.
            if (nade is CsItem.Molotov or CsItem.Incendiary)
            {
                var other = nade == CsItem.Molotov ? CsItem.Incendiary : CsItem.Molotov;
                if (!allowancePerType.TryGetValue(other, out max))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return nades.Count(n => n == nade) < max;
    }

    public static void AllocateNadesToPlayers<T>(
        Stack<CsItem> teamNades,
        ICollection<T> teamPlayers,
        Dictionary<T, ICollection<CsItem>> nadesByPlayer
    ) where T : notnull
    {
        // Copy to avoid mutating the actual player list; shuffle for fairness.
        var shuffled = new List<T>(teamPlayers);
        Utils.Shuffle(shuffled);

        if (shuffled.Count == 0)
        {
            return;
        }

        // Round-robin: each nade goes to the next player able to carry that type.
        // Nades nobody can carry anymore are discarded.
        var playerI = 0;
        while (teamNades.TryPop(out var nextNade))
        {
            for (var attempt = 0; attempt < shuffled.Count; attempt++)
            {
                var player = shuffled[(playerI + attempt) % shuffled.Count];

                if (!nadesByPlayer.TryGetValue(player, out var nadesForPlayer))
                {
                    nadesForPlayer = new List<CsItem>();
                    nadesByPlayer.Add(player, nadesForPlayer);
                }

                if (!CanAcceptNade(nadesForPlayer, nextNade))
                {
                    continue;
                }

                nadesForPlayer.Add(nextNade);
                playerI = (playerI + attempt + 1) % shuffled.Count;
                break;
            }
        }
    }
}