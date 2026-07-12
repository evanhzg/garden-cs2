using GardenRankingsCore.Config;

namespace GardenRankingsCore.Managers;

public record ClutchLayout(int ClutcherCount, int EnemyCount);

/// <summary>
/// Decides when a classic-mode clutch round (1vX / 2vX with X strictly greater)
/// happens and what shape it takes. Guarantees at least MinPerMap clutch rounds
/// per map by forcing one when the random rolls fall behind schedule.
/// </summary>
public class ClutchScheduler
{
    private readonly Random _random;

    public int RoundsThisMap { get; private set; }
    public int ClutchesThisMap { get; private set; }

    private static ClutchConfig Cfg => Configs.GetConfigData().Clutch;

    public ClutchScheduler(Random? random = null)
    {
        _random = random ?? new Random();
    }

    public void OnMapStart()
    {
        RoundsThisMap = 0;
        ClutchesThisMap = 0;
    }

    public void OnRoundPlayed()
    {
        RoundsThisMap++;
    }

    /// <summary>
    /// Whether the NEXT round should be a clutch round.
    /// </summary>
    public bool ShouldTriggerClutch(int teamHumanCount)
    {
        if (!Cfg.Enabled || teamHumanCount < Cfg.MinPlayers)
        {
            return false;
        }

        // Behind on the per-map guarantee? Force one.
        if (ClutchesThisMap < Cfg.MinPerMap &&
            RoundsThisMap >= Cfg.ForceAfterRounds * (ClutchesThisMap + 1))
        {
            return true;
        }

        return _random.Next(100) < Cfg.ChancePercent;
    }

    /// <summary>
    /// Picks the clutch shape for a player count: 1vX at the minimum player count,
    /// otherwise randomly 1vX or 2vX, always with X strictly greater than the
    /// clutcher count.
    /// </summary>
    public ClutchLayout PickLayout(int teamHumanCount)
    {
        var clutchers = 1;
        if (teamHumanCount > Cfg.MinPlayers)
        {
            // 2vX requires X > 2, ie. at least 5 players total.
            clutchers = _random.Next(2) == 0 ? 1 : 2;
        }

        var enemies = teamHumanCount - clutchers;
        if (enemies <= clutchers)
        {
            clutchers = 1;
            enemies = teamHumanCount - 1;
        }

        return new ClutchLayout(clutchers, enemies);
    }

    public void RegisterClutchRound()
    {
        ClutchesThisMap++;
    }
}
