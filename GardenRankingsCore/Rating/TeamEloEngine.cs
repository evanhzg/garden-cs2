using GardenRankingsCore.Config;

namespace GardenRankingsCore.Rating;

/// <summary>
/// Match-level ELO for Competitive Retakes duos/trios.
/// </summary>
public static class TeamEloEngine
{
    /// <summary>
    /// scoreA: 1 = A won, 0 = B won, 0.5 = draw. Returns (deltaA, deltaB).
    /// </summary>
    public static (int DeltaA, int DeltaB) ComputeMatchDeltas(int eloA, int eloB, double scoreA)
    {
        var cfg = Configs.GetConfigData().Competitive;
        var expectedA = 1.0 / (1.0 + Math.Pow(10.0, (eloB - eloA) / 400.0));
        var deltaA = (int) Math.Round(cfg.TeamEloKFactor * (scoreA - expectedA));
        return (deltaA, -deltaA);
    }
}
