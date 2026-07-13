using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GardenRankingsCore.Db;

/// <summary>
/// A ranking season. Exactly one season is active at a time; previous seasons
/// keep all of their stats and rankings and stay queryable forever.
/// </summary>
public class Season
{
    [Key] public int Id { get; set; }

    [MaxLength(64)] public string Name { get; set; } = "";

    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// One row per player ever seen on the server.
/// </summary>
public class PlayerProfile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [Column(TypeName = "bigint")]
    public ulong SteamId { get; set; }

    [MaxLength(128)] public string LastKnownName { get; set; } = "";

    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public int TimeSpentSeconds { get; set; }
}

/// <summary>
/// Hot per-season data: current/peak ELO plus round counters. Detailed stats are
/// aggregated from PlayerRoundRecord (raw, per-round) so the future Discord bot can
/// filter arbitrarily (all time, last map, last N maps, last session, ranked only...).
/// </summary>
public class PlayerSeasonStats
{
    [Key] public int Id { get; set; }

    public int SeasonId { get; set; }

    [Column(TypeName = "bigint")] public ulong SteamId { get; set; }

    public int Elo { get; set; }
    public int PeakElo { get; set; }

    public int RankedRoundsPlayed { get; set; }
    public int RankedRoundsWon { get; set; }
    public int UnrankedRoundsPlayed { get; set; }

    public DateTime? LastRankedRoundAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One row per played round (ranked or not).
/// </summary>
public class RoundRecord
{
    [Key] public long Id { get; set; }

    public int SeasonId { get; set; }

    [MaxLength(128)] public string Map { get; set; } = "";

    public DateTime PlayedAtUtc { get; set; }

    /// <summary>-1 unknown, 0 Pistol, 1 ForceBuy, 2 FullBuy.</summary>
    public int RoundTypeOrdinal { get; set; } = -1;

    public bool IsRanked { get; set; }

    public int TPlayerCount { get; set; }
    public int CtPlayerCount { get; set; }

    /// <summary>2 = T, 3 = CT.</summary>
    public int WinnerTeamNum { get; set; }

    [MaxLength(8)] public string? BombSite { get; set; }
    public bool BombPlanted { get; set; }
    public bool BombDefused { get; set; }
    public bool BombExploded { get; set; }

    public double RoundDurationSeconds { get; set; }

    public ICollection<PlayerRoundRecord> PlayerRecords { get; set; } = new List<PlayerRoundRecord>();
}

/// <summary>
/// One row per player per round: the raw material for every stat, rating and
/// filter the plugin (and later the Discord bot) needs. Map / date / season /
/// ranked flags are denormalized on purpose to make filtering cheap.
/// </summary>
public class PlayerRoundRecord
{
    [Key] public long Id { get; set; }

    public long RoundRecordId { get; set; }

    [ForeignKey(nameof(RoundRecordId))] public RoundRecord? Round { get; set; }

    // Denormalized filters
    public int SeasonId { get; set; }
    [MaxLength(128)] public string Map { get; set; } = "";
    public DateTime PlayedAtUtc { get; set; }
    public bool IsRanked { get; set; }

    [Column(TypeName = "bigint")] public ulong SteamId { get; set; }
    [MaxLength(128)] public string PlayerName { get; set; } = "";
    public int TeamNum { get; set; }
    public bool WonRound { get; set; }

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
    public bool DiedEarly { get; set; }

    // Duels / impact
    public bool OpeningKill { get; set; }
    public bool OpeningDeath { get; set; }
    public int TradeKills { get; set; }
    public bool TradedDeath { get; set; }
    public bool Kast { get; set; }
    public int MultiKillCount { get; set; }
    public int ClutchVersus { get; set; }
    public bool ClutchWon { get; set; }

    // Objectives
    public bool BombPlanted { get; set; }
    public bool BombDefused { get; set; }

    // Flags
    public bool WasAfk { get; set; }

    // Outputs
    public double Rating { get; set; }
    public int EloDelta { get; set; }
    public int EloAfter { get; set; }
}

/// <summary>
/// Competitive Retakes duo/trio ladder entry. A team's identity is its sorted
/// steam ids joined with '-'; the ELO belongs to the roster, not the players.
/// </summary>
public class CrTeamStats
{
    [Key] public int Id { get; set; }

    public int SeasonId { get; set; }

    [MaxLength(96)] public string TeamKey { get; set; } = "";
    [MaxLength(256)] public string PlayerNames { get; set; } = "";
    public int TeamSize { get; set; }

    public int Elo { get; set; }
    public int PeakElo { get; set; }

    public int MatchesPlayed { get; set; }
    public int MatchesWon { get; set; }
    public int MatchesDrawn { get; set; }
    public int RoundsWon { get; set; }
    public int RoundsLost { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One row per Competitive Retakes match.
/// </summary>
public class CrMatch
{
    [Key] public long Id { get; set; }

    public int SeasonId { get; set; }
    [MaxLength(128)] public string Map { get; set; } = "";

    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }

    [MaxLength(96)] public string TeamAKey { get; set; } = "";
    [MaxLength(96)] public string TeamBKey { get; set; } = "";
    [MaxLength(256)] public string TeamAName { get; set; } = "";
    [MaxLength(256)] public string TeamBName { get; set; } = "";
    public int TeamSize { get; set; }

    public int ScoreA { get; set; }
    public int ScoreB { get; set; }

    /// <summary>"A", "B", "draw" or "cancelled".</summary>
    [MaxLength(16)] public string Result { get; set; } = "";

    public int EloDeltaA { get; set; }
    public int EloDeltaB { get; set; }
}

/// <summary>
/// Garden merged plugin (ROADMAP R3): a server admin. Config-file owners are
/// NOT stored here — they live in the plugin config and always win.
/// </summary>
public class GardenAdmin
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public ulong SteamId { get; set; }

    [MaxLength(128)] public string Name { get; set; } = "";

    /// <summary>1 = Moderator, 2 = Admin, 3 = Owner (GardenRetakes AdminLevel).</summary>
    public int Level { get; set; }

    public ulong AddedBy { get; set; }
    public DateTime AddedAtUtc { get; set; }
}

/// <summary>
/// Garden (W2): a server ban. Null ExpiresAtUtc = permanent. Written by the
/// website admin panel and !gban; enforced on connect.
/// </summary>
public class GardenBan
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public ulong SteamId { get; set; }

    [MaxLength(128)] public string Name { get; set; } = "";
    [MaxLength(256)] public string Reason { get; set; } = "";
    public ulong BannedBy { get; set; }
    public DateTime BannedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>
/// Garden (W2): a display-name override chosen on the website. When present it
/// wins over the Steam name everywhere (in game, ladder, stats). Deleting the
/// row reverts to the Steam name.
/// </summary>
public class GardenNameOverride
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public ulong SteamId { get; set; }

    [MaxLength(64)] public string Name { get; set; } = "";
}

/// <summary>
/// Website-owned: the live state of the server's current match (JSON blob),
/// updated at the end of every round. The website polls this for the Live Spectator page.
/// </summary>
public class WebLiveMatch
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int ServerId { get; set; }

    [Column(TypeName = "longtext")]
    public string Data { get; set; } = "";

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Garden merged plugin (Duels mode): one row per completed 1v1 — feeds the
/// website duels ladder and future Discord stats.
/// </summary>
public class DuelRecord
{
    [Key] public long Id { get; set; }

    public int SeasonId { get; set; }
    [MaxLength(128)] public string Map { get; set; } = "";
    public DateTime PlayedAtUtc { get; set; }
    [MaxLength(64)] public string ArenaName { get; set; } = "";

    public ulong WinnerSteamId { get; set; }
    [MaxLength(128)] public string WinnerName { get; set; } = "";
    public ulong LoserSteamId { get; set; }
    [MaxLength(128)] public string LoserName { get; set; } = "";

    public bool IsChallenge { get; set; }

    /// <summary>Final score when this kill finished a challenge ("3-1"), else "".</summary>
    [MaxLength(16)] public string ChallengeScore { get; set; } = "";
}

/// <summary>
/// Garden merged plugin (ROADMAP R3): audit log of every admin action
/// (admin add/remove, kick, slay, map change, rcon).
/// </summary>
public class GardenAdminLogEntry
{
    [Key] public long Id { get; set; }

    public DateTime AtUtc { get; set; }
    public ulong ActorSteamId { get; set; }
    [MaxLength(128)] public string ActorName { get; set; } = "";
    [MaxLength(32)] public string Action { get; set; } = "";
    public ulong TargetSteamId { get; set; }
    [MaxLength(128)] public string TargetName { get; set; } = "";
    [MaxLength(256)] public string Detail { get; set; } = "";
}

public class NemesisRecord
{
    public ulong KillerSteamId { get; set; }
    public ulong VictimSteamId { get; set; }
    public int Kills { get; set; }
}

/// <summary>
/// Garden (W2): Heatmap coordinate data for kills and deaths.
/// </summary>
public class GardenHeatmap
{
    [Key] public long Id { get; set; }
    
    [Column(TypeName = "bigint")] public ulong VictimSteamId { get; set; }
    [Column(TypeName = "bigint")] public ulong AttackerSteamId { get; set; }
    [MaxLength(128)] public string MapName { get; set; } = "";
    
    public float VictimX { get; set; }
    public float VictimY { get; set; }
    public float VictimZ { get; set; }
    
    public float AttackerX { get; set; }
    public float AttackerY { get; set; }
    public float AttackerZ { get; set; }
    
    [MaxLength(64)] public string Weapon { get; set; } = "";
    public bool IsHeadshot { get; set; }
    public bool IsRanked { get; set; }
    [MaxLength(16)] public string? Site { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
