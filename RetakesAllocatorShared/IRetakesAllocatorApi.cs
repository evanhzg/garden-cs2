namespace RetakesAllocatorShared;

/// <summary>
/// Cross-plugin API exposed by the Garden retakes allocator through a
/// CounterStrikeSharp plugin capability. Consumers (eg. GardenRankings) obtain it with:
/// <code>new PluginCapability&lt;IRetakesAllocatorApi&gt;(RetakesAllocatorApiCapability.Name).Get()</code>
/// Team numbers use the engine values (2 = Terrorist, 3 = Counter-Terrorist).
/// Round type ordinals: 0 = Pistol, 1 = HalfBuy (one-sided Force Buy), 2 = FullBuy.
/// </summary>
public interface IRetakesAllocatorApi
{
    /// <summary>The round type of the ongoing round, or null when unknown (eg. warmup).</summary>
    int? CurrentRoundTypeOrdinal { get; }

    /// <summary>"Pistol", "HalfBuy" or "FullBuy", or null when unknown.</summary>
    string? CurrentRoundTypeName { get; }

    /// <summary>On a force-buy round, the team number that is force-buying; null otherwise.</summary>
    int? ForceBuyTeamNum { get; }

    /// <summary>
    /// The round type a given team is effectively playing (the full-buying team of a
    /// force-buy round effectively plays FullBuy). Null when unknown.
    /// </summary>
    int? GetEffectiveRoundTypeOrdinal(int teamNum);

    /// <summary>
    /// Forces the next round's type (0 Pistol, 1 HalfBuy/ForceBuy, 2 FullBuy).
    /// Null clears the override. Used by match modes (eg. Competitive Retakes).
    /// </summary>
    void SetNextRoundTypeOverride(int? roundTypeOrdinal);

    /// <summary>
    /// When the next round is a force-buy round, forces WHICH team force-buys
    /// (2 = T, 3 = CT) instead of picking randomly. One-shot; consumed at allocation.
    /// </summary>
    void SetNextForceBuyTeam(int? teamNum);
}

public static class RetakesAllocatorApiCapability
{
    public const string Name = "retakes_allocator:api";
}
