using GardenRankingsCore.Config;
using GardenRankingsCore.Models;

namespace GardenRankingsCore.Rating;

/// <summary>
/// Retakes-adapted, HLTV-2.0-inspired per-round rating.
///
/// Each component is normalized against a configurable "average round" baseline so
/// that an average performance scores ~1.00. Baselines scale with the number of
/// enemies (team size) and with the round type, making the rating comparable across
/// pistol rounds, force buys, full buys and any team size.
///
/// rating = (Wk*killScore + Wd*damageScore + Ws*survivalScore + Wkast*kastScore + Wi*impactScore) / SumW
///          * RoundTypeRatingScale
///
/// See Docs/RATING.md for the full explanation of every term and default.
/// </summary>
public static class RatingEngine
{
    public static double ComputeRoundRating(PlayerRoundStats s, RoundContext ctx)
    {
        var cfg = Configs.GetConfigData().Rating;
        var scale = cfg.GetRoundTypeScale(ctx.RoundType);

        var enemies = Math.Max(1, ctx.EnemyCountFor(s.TeamNum));
        var enemyScale = enemies / 5.0;

        var expectedKills = Math.Max(0.05, cfg.ExpectedKillsPerRound * enemyScale * scale.KillExpectationScale);
        var expectedDamage = Math.Max(5.0, cfg.ExpectedDamagePerRound * enemyScale * scale.DamageExpectationScale);

        var killScore = s.Kills / expectedKills;
        var damageScore = s.Damage / expectedDamage;
        var survivalScore = s.Survived ? 1.0 / Math.Max(0.05, cfg.ExpectedSurvivalRate) : 0.0;
        var kastScore = s.Kast ? 1.0 / Math.Max(0.05, cfg.ExpectedKastRate) : 0.0;
        var impactScore = ComputeImpact(s) / Math.Max(0.05, cfg.ExpectedImpactPerRound);

        var w = cfg.Weights;
        var rating =
            (w.Kill * killScore +
             w.Damage * damageScore +
             w.Survival * survivalScore +
             w.Kast * kastScore +
             w.Impact * impactScore) / w.Total;

        rating *= scale.RatingScale;

        return Math.Clamp(rating, cfg.RatingClampMin, cfg.RatingClampMax);
    }

    /// <summary>
    /// Raw (un-normalized) impact contribution of a round: opening duels, multi-kills,
    /// clutches, objectives (retakes' bread and butter), trades and utility usage.
    /// </summary>
    public static double ComputeImpact(PlayerRoundStats s)
    {
        var b = Configs.GetConfigData().Rating.Impact;

        var impact = 0.0;

        if (s.OpeningKill)
        {
            impact += b.OpeningKill;
        }

        if (s.OpeningDeath)
        {
            impact += b.OpeningDeathPenalty;
        }

        impact += s.Kills switch
        {
            2 => b.MultiKill2,
            3 => b.MultiKill3,
            4 => b.MultiKill4,
            >= 5 => b.MultiKill5,
            _ => 0.0,
        };

        if (s.ClutchWon)
        {
            impact += b.ClutchWinBase + b.ClutchWinPerEnemy * Math.Max(0, s.ClutchVersus - 1);
        }

        if (s.Planted)
        {
            impact += b.BombPlant;
        }

        if (s.Defused)
        {
            impact += b.BombDefuse;
        }

        impact += s.TradeKills * b.TradeKill;
        impact += s.FlashAssists * b.FlashAssist;
        impact += s.UtilityDamage / 100.0 * b.UtilityDamagePer100;

        if (s.KilledTeammate)
        {
            impact += b.TeamKillPenalty;
        }

        return impact;
    }

    /// <summary>
    /// Computes and stores ratings for every player of a finished round.
    /// </summary>
    public static void ComputeRatings(ICollection<PlayerRoundStats> players, RoundContext ctx)
    {
        foreach (var player in players)
        {
            player.Rating = player.WasAfk ? 0.0 : ComputeRoundRating(player, ctx);
        }
    }
}
