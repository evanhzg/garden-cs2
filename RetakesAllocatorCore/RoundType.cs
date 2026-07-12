using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;

namespace RetakesAllocatorCore;

public enum RoundType
{
    Pistol,
    HalfBuy,
    FullBuy,
}

public static class RoundTypeHelpers
{
    public static List<RoundType> GetRoundTypes()
    {
        return Enum.GetValues<RoundType>().ToList();
    }

    public static IEnumerable<CsItem> GetRandomUtilForRoundType(RoundType roundType, CsTeam team)
    {
        // Ts (defending the bomb) only ever get HE/flashbangs from the random pool
        // (team smokes are limited to one and handled by NadeHelpers).
        if (roundType == RoundType.Pistol)
        {
            return new List<CsItem>
            {
                Utils.Choice(team == CsTeam.Terrorist
                    ? new List<CsItem>
                    {
                        CsItem.Flashbang,
                        CsItem.HEGrenade,
                    }
                    : new List<CsItem>
                    {
                        CsItem.Flashbang,
                        CsItem.Smoke,
                    }),
            };
        }

        // All util options are available on buy rounds (Ts are restricted to HE/flash)
        var possibleItems = team == CsTeam.Terrorist
            ? new HashSet<CsItem>
            {
                CsItem.Flashbang,
                CsItem.HEGrenade,
            }
            : new HashSet<CsItem>
            {
                CsItem.Flashbang,
                CsItem.Smoke,
                CsItem.HEGrenade,
                CsItem.Incendiary,
            };

        var firstUtil = Utils.Choice(possibleItems);

        // Everyone gets one util
        var randomUtil = new List<CsItem>
        {
            firstUtil,
        };

        // 50% chance to get an extra util item
        if (new Random().NextDouble() < .5)
        {
            // We cant give people duplicate of anything other than a flash though
            if (firstUtil != CsItem.Flashbang)
            {
                possibleItems.Remove(firstUtil);
            }

            randomUtil.Add(Utils.Choice(possibleItems));
        }

        return randomUtil;
    }

    public static CsItem GetArmorForRoundType(RoundType roundType) =>
        roundType == RoundType.Pistol ? CsItem.Kevlar : CsItem.KevlarHelmet;

    public static RoundType? ParseRoundType(string roundType)
    {
        return roundType.ToLower() switch
        {
            "f" => RoundType.FullBuy,
            "full" => RoundType.FullBuy,
            "fullbuy" => RoundType.FullBuy,
            "h" => RoundType.HalfBuy,
            "half" => RoundType.HalfBuy,
            "halfbuy" => RoundType.HalfBuy,
            "force" => RoundType.HalfBuy,
            "forcebuy" => RoundType.HalfBuy,
            "p" => RoundType.Pistol,
            "pistol" => RoundType.Pistol,
            _ => null,
        };
    }

    public static string TranslateRoundTypeName(RoundType roundType)
    {
        return Translator.Instance[$"roundtype.{roundType}"];
    }
}
