namespace GardenRankingsCore.Models;

/// <summary>
/// Round types as understood by the ranking system.
/// Ordinals 0..2 match the Garden allocator's RoundType enum
/// (HalfBuy is the allocator's one-sided Force Buy).
/// </summary>
public enum RetakesRoundType
{
    Unknown = -1,
    Pistol = 0,
    ForceBuy = 1,
    FullBuy = 2,
}

public static class TeamNums
{
    public const int Spectator = 1;
    public const int T = 2;
    public const int Ct = 3;
}

/// <summary>
/// Everything known about a finished round, independent of any player.
/// </summary>
public class RoundContext
{
    public string Map { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public RetakesRoundType RoundType { get; set; } = RetakesRoundType.Unknown;
    public bool IsRanked { get; set; }
    public int TPlayerCount { get; set; }
    public int CtPlayerCount { get; set; }
    public int WinnerTeamNum { get; set; }
    public string? BombSite { get; set; }
    public bool BombPlanted { get; set; }
    public bool BombDefused { get; set; }
    public bool BombExploded { get; set; }
    public double RoundDurationSeconds { get; set; }

    public int EnemyCountFor(int teamNum) => teamNum == TeamNums.T ? CtPlayerCount : TPlayerCount;
}

/// <summary>
/// Per-player, per-round stat sheet. Filled by the plugin's RoundDataCollector,
/// then completed by the rating and ELO engines, then persisted.
/// </summary>
public class PlayerRoundStats
{
    public ulong SteamId { get; set; }
    public string PlayerName { get; set; } = "";
    public int TeamNum { get; set; }
    public int EloBefore { get; set; }

    // Combat
    public int Kills { get; set; }
    public int Headshots { get; set; }
    public int Assists { get; set; }
    public int FlashAssists { get; set; }
    public int Damage { get; set; }
    public int UtilityDamage { get; set; }
    public int EnemiesFlashed { get; set; }
    public double EnemyBlindDuration { get; set; }

    // Life
    public bool Died { get; set; }
    public double? DiedAtSeconds { get; set; }
    public bool WasTeamKilled { get; set; }
    public bool KilledTeammate { get; set; }

    // Duels / impact
    public bool OpeningKill { get; set; }
    public bool OpeningDeath { get; set; }
    public int TradeKills { get; set; }
    public bool TradedDeath { get; set; }
    public int ClutchVersus { get; set; }
    public bool ClutchWon { get; set; }

    // Objectives
    public bool Planted { get; set; }
    public bool Defused { get; set; }

    // Flags
    public bool WasAfk { get; set; }
    public bool WonRound { get; set; }

    // Derived
    public bool Survived => !Died;
    public bool Kast => Kills > 0 || Assists > 0 || Survived || TradedDeath;
    public bool DiedEarly { get; set; }

    // Outputs
    public double Rating { get; set; }
    public int EloDelta { get; set; }
    public int EloAfter { get; set; }
}
