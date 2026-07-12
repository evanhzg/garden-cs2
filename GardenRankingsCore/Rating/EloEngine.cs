using GardenRankingsCore.Config;
using GardenRankingsCore.Models;

namespace GardenRankingsCore.Rating;

/// <summary>
/// Per-round ELO engine.
///
/// Expectation model (per team):
///   expected = clamp( TBase + roundTypeOffset + playerAdvantage + eloAdjustment , 0.05, 0.95 )
/// where
///   - TBase reflects the retakes asymmetry (defending Ts win more rounds),
///   - playerAdvantage shifts expectation for uneven team sizes,
///   - eloAdjustment = (logistic(avgEloDiff) - 0.5) * EloInfluenceOnExpectation.
///
/// Per player:
///   delta = K * (score - expected) * performanceMultiplier
/// Winners with a high round rating gain more; losers with a high rating lose less.
///
/// Loss mitigations (strongest applies, no stacking): teamkilled, killed early,
/// an enemy had a great round. AFK players never gain or lose ELO.
/// </summary>
public static class EloEngine
{
    public static double Logistic(double eloDiff)
    {
        var divisor = Math.Max(1.0, Configs.GetConfigData().Elo.EloDivisor);
        return 1.0 / (1.0 + Math.Pow(10.0, -eloDiff / divisor));
    }

    public static double ExpectedWinProbability(
        int teamNum,
        double myAvgElo,
        double oppAvgElo,
        int myCount,
        int oppCount,
        RetakesRoundType roundType
    )
    {
        var cfg = Configs.GetConfigData().Elo;

        var tOffset = cfg.TWinProbabilityOffsetByRoundType.TryGetValue(roundType, out var off) ? off : 0.0;
        var tBase = cfg.TBaseWinProbability + tOffset;
        var baseProbability = teamNum == TeamNums.T ? tBase : 1.0 - tBase;

        var advantage = (myCount - oppCount) * cfg.PlayerCountAdvantageWinProbability;
        var eloAdjustment = (Logistic(myAvgElo - oppAvgElo) - 0.5) * cfg.EloInfluenceOnExpectation;

        return Math.Clamp(baseProbability + advantage + eloAdjustment, 0.05, 0.95);
    }

    /// <summary>
    /// Computes EloDelta/EloAfter for every player. Ratings must already be computed.
    /// When the round is not ranked, deltas are zero and EloAfter keeps the current ELO.
    /// </summary>
    public static void ApplyEloDeltas(ICollection<PlayerRoundStats> players, RoundContext ctx)
    {
        var cfg = Configs.GetConfigData().Elo;

        if (!ctx.IsRanked)
        {
            foreach (var p in players)
            {
                p.EloDelta = 0;
                p.EloAfter = p.EloBefore;
            }

            return;
        }

        var tPlayers = players.Where(p => p.TeamNum == TeamNums.T).ToList();
        var ctPlayers = players.Where(p => p.TeamNum == TeamNums.Ct).ToList();

        if (tPlayers.Count == 0 || ctPlayers.Count == 0)
        {
            foreach (var p in players)
            {
                p.EloDelta = 0;
                p.EloAfter = p.EloBefore;
            }

            return;
        }

        var tAvg = tPlayers.Average(p => (double) p.EloBefore);
        var ctAvg = ctPlayers.Average(p => (double) p.EloBefore);

        var tExpected = ExpectedWinProbability(
            TeamNums.T, tAvg, ctAvg, tPlayers.Count, ctPlayers.Count, ctx.RoundType);
        var ctExpected = 1.0 - tExpected;

        foreach (var p in players)
        {
            if (p.TeamNum is not (TeamNums.T or TeamNums.Ct))
            {
                p.EloDelta = 0;
                p.EloAfter = p.EloBefore;
                continue;
            }

            if (p.WasAfk && cfg.AfkEloProtection)
            {
                p.EloDelta = 0;
                p.EloAfter = p.EloBefore;
                continue;
            }

            var expected = p.TeamNum == TeamNums.T ? tExpected : ctExpected;
            var won = ctx.WinnerTeamNum == p.TeamNum;
            var score = won ? 1.0 : 0.0;

            var raw = cfg.KFactor * (score - expected);

            var performance = 1.0 + (p.Rating - cfg.PerformanceRatingReference) * cfg.PerformanceInfluence;
            performance = Math.Clamp(performance, cfg.PerformanceMultiplierMin, cfg.PerformanceMultiplierMax);

            // Winners: high rating -> bigger gain. Losers: high rating -> smaller loss.
            var delta = won ? raw * performance : raw * (2.0 - performance);

            if (!won)
            {
                var mitigation = ComputeLossMitigation(p, players);
                delta *= 1.0 - Math.Clamp(mitigation, 0.0, 1.0);
            }

            p.EloDelta = (int) Math.Round(delta);
            p.EloAfter = Math.Clamp(p.EloBefore + p.EloDelta, cfg.MinElo, cfg.MaxElo);
        }
    }

    /// <summary>
    /// 0 = full loss, 1 = loss cancelled. The strongest applicable mitigation applies.
    /// </summary>
    public static double ComputeLossMitigation(PlayerRoundStats player, ICollection<PlayerRoundStats> allPlayers)
    {
        var cfg = Configs.GetConfigData().Elo;
        var mitigation = 0.0;

        if (player.WasTeamKilled)
        {
            mitigation = Math.Max(mitigation, cfg.MitigationTeamKilled);
        }

        if (player.DiedEarly)
        {
            mitigation = Math.Max(mitigation, cfg.MitigationEarlyDeath);
        }

        var enemyHadGreatRound = allPlayers.Any(p =>
            p.TeamNum != player.TeamNum &&
            p.TeamNum is TeamNums.T or TeamNums.Ct &&
            (p.Rating >= cfg.EnemyGreatRoundRatingThreshold || p.Kills >= cfg.EnemyGreatRoundKills));
        if (enemyHadGreatRound)
        {
            mitigation = Math.Max(mitigation, cfg.MitigationEnemyGreatRound);
        }

        return mitigation;
    }

    /// <summary>
    /// Marks DiedEarly on players who died fast without contributing damage.
    /// </summary>
    public static void FlagEarlyDeaths(ICollection<PlayerRoundStats> players)
    {
        var cfg = Configs.GetConfigData().Elo;
        foreach (var p in players)
        {
            p.DiedEarly =
                p.Died &&
                p.DiedAtSeconds is not null &&
                p.DiedAtSeconds <= cfg.EarlyDeathSeconds &&
                p.Damage <= cfg.EarlyDeathMaxDamage;
        }
    }
}
