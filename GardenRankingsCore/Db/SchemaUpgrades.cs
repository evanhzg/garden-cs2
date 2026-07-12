using GardenRankingsCore.Config;
using Microsoft.EntityFrameworkCore;

namespace GardenRankingsCore.Db;

/// <summary>
/// EnsureCreated() only builds the schema when the database file/schema does not
/// exist yet, so tables added in later plugin versions must be created explicitly.
/// Everything here is idempotent (CREATE TABLE IF NOT EXISTS).
/// </summary>
public static class SchemaUpgrades
{
    public static void Apply(Db db)
    {
        var isMySql = Configs.IsLoaded() &&
                      Configs.GetConfigData().DatabaseProvider == DatabaseProvider.MySql;

        var autoIncrementPk = isMySql
            ? "INT NOT NULL AUTO_INCREMENT PRIMARY KEY"
            : "INTEGER PRIMARY KEY AUTOINCREMENT";
        var autoIncrementBigPk = isMySql
            ? "BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY"
            : "INTEGER PRIMARY KEY AUTOINCREMENT";
        var dateTime = isMySql ? "DATETIME(6)" : "TEXT";

        db.Database.ExecuteSqlRaw($"""
            CREATE TABLE IF NOT EXISTS CrTeamStats (
                Id {autoIncrementPk},
                SeasonId INT NOT NULL,
                TeamKey VARCHAR(96) NOT NULL,
                PlayerNames VARCHAR(256) NOT NULL,
                TeamSize INT NOT NULL,
                Elo INT NOT NULL,
                PeakElo INT NOT NULL,
                MatchesPlayed INT NOT NULL,
                MatchesWon INT NOT NULL,
                MatchesDrawn INT NOT NULL,
                RoundsWon INT NOT NULL,
                RoundsLost INT NOT NULL,
                UpdatedAtUtc {dateTime} NOT NULL
            )
            """);

        db.Database.ExecuteSqlRaw($"""
            CREATE TABLE IF NOT EXISTS CrMatches (
                Id {autoIncrementBigPk},
                SeasonId INT NOT NULL,
                Map VARCHAR(128) NOT NULL,
                StartedAtUtc {dateTime} NOT NULL,
                EndedAtUtc {dateTime} NULL,
                TeamAKey VARCHAR(96) NOT NULL,
                TeamBKey VARCHAR(96) NOT NULL,
                TeamAName VARCHAR(256) NOT NULL,
                TeamBName VARCHAR(256) NOT NULL,
                TeamSize INT NOT NULL,
                ScoreA INT NOT NULL,
                ScoreB INT NOT NULL,
                Result VARCHAR(16) NOT NULL,
                EloDeltaA INT NOT NULL,
                EloDeltaB INT NOT NULL
            )
            """);

        // Garden merged plugin (ROADMAP R3): admin storage + audit log.
        var steamIdType = isMySql ? "BIGINT UNSIGNED" : "INTEGER";
        db.Database.ExecuteSqlRaw($"""
            CREATE TABLE IF NOT EXISTS GardenAdmins (
                SteamId {steamIdType} NOT NULL PRIMARY KEY,
                Name VARCHAR(128) NOT NULL,
                Level INT NOT NULL,
                AddedBy {steamIdType} NOT NULL,
                AddedAtUtc {dateTime} NOT NULL
            )
            """);

        db.Database.ExecuteSqlRaw($"""
            CREATE TABLE IF NOT EXISTS GardenAdminLog (
                Id {autoIncrementBigPk},
                AtUtc {dateTime} NOT NULL,
                ActorSteamId {steamIdType} NOT NULL,
                ActorName VARCHAR(128) NOT NULL,
                Action VARCHAR(32) NOT NULL,
                TargetSteamId {steamIdType} NOT NULL,
                TargetName VARCHAR(128) NOT NULL,
                Detail VARCHAR(256) NOT NULL
            )
            """);

        // Garden (W2): bans + display-name overrides.
        db.Database.ExecuteSqlRaw($"""
            CREATE TABLE IF NOT EXISTS GardenBans (
                SteamId {steamIdType} NOT NULL PRIMARY KEY,
                Name VARCHAR(128) NOT NULL,
                Reason VARCHAR(256) NOT NULL,
                BannedBy {steamIdType} NOT NULL,
                BannedAtUtc {dateTime} NOT NULL,
                ExpiresAtUtc {dateTime} NULL
            )
            """);

        db.Database.ExecuteSqlRaw($"""
            CREATE TABLE IF NOT EXISTS GardenNameOverrides (
                SteamId {steamIdType} NOT NULL PRIMARY KEY,
                Name VARCHAR(64) NOT NULL
            )
            """);

        // Garden (W2): website-owned player card (avatar/bio/country). The plugin
        // never reads it, but it owns the shared-DB schema, so it creates the table
        // here too — the whole W2 schema then self-applies on plugin load.
        db.Database.ExecuteSqlRaw($"""
            CREATE TABLE IF NOT EXISTS GardenWebProfiles (
                SteamId {steamIdType} NOT NULL PRIMARY KEY,
                AvatarUrl VARCHAR(512) NULL,
                Bio VARCHAR(280) NULL,
                Country VARCHAR(2) NULL,
                UpdatedAt {dateTime} NOT NULL
            )
            """);

        // Garden merged plugin (Duels mode): one row per completed 1v1.
        db.Database.ExecuteSqlRaw($"""
            CREATE TABLE IF NOT EXISTS DuelRecords (
                Id {autoIncrementBigPk},
                SeasonId INT NOT NULL,
                Map VARCHAR(128) NOT NULL,
                PlayedAtUtc {dateTime} NOT NULL,
                ArenaName VARCHAR(64) NOT NULL,
                WinnerSteamId {steamIdType} NOT NULL,
                WinnerName VARCHAR(128) NOT NULL,
                LoserSteamId {steamIdType} NOT NULL,
                LoserName VARCHAR(128) NOT NULL,
                IsChallenge TINYINT(1) NOT NULL,
                ChallengeScore VARCHAR(16) NOT NULL
            )
            """);

        try
        {
            db.Database.ExecuteSqlRaw(
                "CREATE INDEX IF NOT EXISTS IX_DuelRecords_SeasonId ON DuelRecords (SeasonId)");
            db.Database.ExecuteSqlRaw(
                "CREATE UNIQUE INDEX IF NOT EXISTS IX_CrTeamStats_SeasonId_TeamKey " +
                "ON CrTeamStats (SeasonId, TeamKey)");
            db.Database.ExecuteSqlRaw(
                "CREATE INDEX IF NOT EXISTS IX_CrMatches_SeasonId ON CrMatches (SeasonId)");
        }
        catch
        {
            // MySQL < 8.0.29 has no IF NOT EXISTS for indexes; a duplicate-index
            // error here just means the index already exists.
        }
    }
}
