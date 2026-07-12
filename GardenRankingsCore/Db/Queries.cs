using GardenRankingsCore.Config;
using GardenRankingsCore.Models;
using Microsoft.EntityFrameworkCore;

namespace GardenRankingsCore.Db;

public record PlacementInfo(int Rank, int TotalRanked, int Elo);

public record TopPlayerInfo(ulong SteamId, string Name, int Elo, int RankedRoundsPlayed);

public record SeasonBestInfo(ulong SteamId, string Name, int PeakElo, string SeasonName);

public record PlayerSeasonSummary(
    int RoundsPlayed,
    int RankedRoundsPlayed,
    int Kills,
    int Deaths,
    int Assists,
    int Headshots,
    double AverageDamagePerRound,
    double KastPercent,
    double AverageRating,
    int ClutchWins,
    int OpeningKills,
    int BombPlants,
    int BombDefuses,
    int Elo,
    int PeakElo
);

public class Queries
{
    public static void Initialize()
    {
        using (var db = new Db())
        {
            db.Database.EnsureCreated();
            SchemaUpgrades.Apply(db);
        }

        EnsureActiveSeason();
    }

    public static void Disconnect()
    {
        Db.Disconnect();
    }

    public static void Wipe()
    {
        using var db = new Db();
        db.PlayerRoundRecords.ExecuteDelete();
        db.RoundRecords.ExecuteDelete();
        db.PlayerSeasonStats.ExecuteDelete();
        db.PlayerProfiles.ExecuteDelete();
        db.CrMatches.ExecuteDelete();
        db.CrTeamStats.ExecuteDelete();
        db.Seasons.ExecuteDelete();
    }

    #region Seasons

    public static Season EnsureActiveSeason()
    {
        using var db = new Db();
        var active = db.Seasons.AsNoTracking().FirstOrDefault(s => s.IsActive);
        if (active is not null)
        {
            return active;
        }

        var count = db.Seasons.Count();
        var prefix = Configs.IsLoaded() ? Configs.GetConfigData().Season.SeasonNamePrefix : "Season";
        var season = new Season
        {
            Name = $"{prefix} {count + 1}",
            StartedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.Seasons.Add(season);
        db.SaveChanges();
        db.Entry(season).State = EntityState.Detached;
        return season;
    }

    public static Season GetActiveSeason()
    {
        return EnsureActiveSeason();
    }

    public static List<Season> GetAllSeasons()
    {
        using var db = new Db();
        return db.Seasons.AsNoTracking().OrderBy(s => s.Id).ToList();
    }

    public static Season StartNewSeason(string? name = null)
    {
        using var db = new Db();

        var active = db.Seasons.FirstOrDefault(s => s.IsActive);
        if (active is not null)
        {
            active.IsActive = false;
            active.EndedAtUtc = DateTime.UtcNow;
            db.Entry(active).State = EntityState.Modified;
        }

        var count = db.Seasons.Count();
        var prefix = Configs.IsLoaded() ? Configs.GetConfigData().Season.SeasonNamePrefix : "Season";
        var season = new Season
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"{prefix} {count + 1}" : name!.Trim(),
            StartedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.Seasons.Add(season);
        db.SaveChanges();
        db.Entry(season).State = EntityState.Detached;
        if (active is not null)
        {
            db.Entry(active).State = EntityState.Detached;
        }

        return season;
    }

    #endregion

    #region Players

    /// <summary>W2: the website-chosen display name, or null.</summary>
    public static string? GetNameOverride(ulong steamId)
    {
        using var db = new Db();
        return db.GardenNameOverrides.FirstOrDefault(o => o.SteamId == steamId)?.Name;
    }

    /// <summary>W2: the active ban for a player, or null (expired bans are pruned).</summary>
    public static GardenBan? GetActiveBan(ulong steamId)
    {
        using var db = new Db();
        var ban = db.GardenBans.FirstOrDefault(b => b.SteamId == steamId);
        if (ban is null)
        {
            return null;
        }

        if (ban.ExpiresAtUtc is { } expiry && expiry <= DateTime.UtcNow)
        {
            db.GardenBans.Where(b => b.SteamId == steamId).ExecuteDelete();
            return null;
        }

        return ban;
    }

    public static void UpsertGardenBan(ulong steamId, string name, string reason, ulong bannedBy, DateTime? expiresAtUtc)
    {
        using var db = new Db();
        var existing = db.GardenBans.AsTracking().FirstOrDefault(b => b.SteamId == steamId);
        if (existing is null)
        {
            db.GardenBans.Add(new GardenBan
            {
                SteamId = steamId, Name = name, Reason = reason,
                BannedBy = bannedBy, BannedAtUtc = DateTime.UtcNow, ExpiresAtUtc = expiresAtUtc,
            });
        }
        else
        {
            existing.Name = name;
            existing.Reason = reason;
            existing.BannedBy = bannedBy;
            existing.ExpiresAtUtc = expiresAtUtc;
        }

        db.SaveChanges();
    }

    public static void DeleteGardenBan(ulong steamId)
    {
        using var db = new Db();
        db.GardenBans.Where(b => b.SteamId == steamId).ExecuteDelete();
    }

    public static void UpsertPlayerProfile(ulong steamId, string name)
    {
        if (steamId == 0)
        {
            return;
        }

        using var db = new Db();

        // W2: a website-chosen name override always wins over the Steam name.
        var overrideName = db.GardenNameOverrides.FirstOrDefault(o => o.SteamId == steamId)?.Name;
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            name = overrideName;
        }

        var now = DateTime.UtcNow;
        var profile = db.PlayerProfiles.AsNoTracking().FirstOrDefault(p => p.SteamId == steamId);
        if (profile is null)
        {
            profile = new PlayerProfile
            {
                SteamId = steamId,
                LastKnownName = name,
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now,
            };
            db.PlayerProfiles.Add(profile);
        }
        else
        {
            profile.LastKnownName = name;
            profile.LastSeenAtUtc = now;
            db.Entry(profile).State = EntityState.Modified;
        }

        db.SaveChanges();
        db.Entry(profile).State = EntityState.Detached;
    }

    public static PlayerSeasonStats? GetSeasonStats(int seasonId, ulong steamId)
    {
        using var db = new Db();
        return db.PlayerSeasonStats.AsNoTracking()
            .FirstOrDefault(s => s.SeasonId == seasonId && s.SteamId == steamId);
    }

    /// <summary>
    /// Current ELO for a set of players in a season. Missing players are absent
    /// from the result (callers should fall back to Elo.StartingElo).
    /// </summary>
    public static Dictionary<ulong, int> GetElos(int seasonId, ICollection<ulong> steamIds)
    {
        using var db = new Db();
        return db.PlayerSeasonStats.AsNoTracking()
            .Where(s => s.SeasonId == seasonId && steamIds.Contains(s.SteamId))
            .ToDictionary(s => s.SteamId, s => s.Elo);
    }

    /// <summary>
    /// Test/admin helper: directly sets a player's ELO for the season. Marks at
    /// least one ranked round played so the player shows up on the ladder.
    /// </summary>
    public static void SetElo(int seasonId, ulong steamId, int elo)
    {
        using var db = new Db();
        var now = DateTime.UtcNow;
        var stats = db.PlayerSeasonStats
            .FirstOrDefault(s => s.SeasonId == seasonId && s.SteamId == steamId);
        if (stats is null)
        {
            stats = new PlayerSeasonStats
            {
                SeasonId = seasonId,
                SteamId = steamId,
                Elo = elo,
                PeakElo = elo,
                RankedRoundsPlayed = 1,
                UpdatedAtUtc = now,
            };
            db.PlayerSeasonStats.Add(stats);
        }
        else
        {
            stats.Elo = elo;
            stats.PeakElo = Math.Max(stats.PeakElo, elo);
            stats.RankedRoundsPlayed = Math.Max(1, stats.RankedRoundsPlayed);
            stats.UpdatedAtUtc = now;
            db.Entry(stats).State = EntityState.Modified;
        }

        db.SaveChanges();
    }

    #endregion

    #region Round persistence

    /// <summary>
    /// Persists a finished round: the round row, one row per player, and the
    /// per-season hot stats (ELO, peak, counters). Returns the updated season
    /// stats by steam id so callers can refresh scoreboards and records.
    /// </summary>
    public static Dictionary<ulong, PlayerSeasonStats> PersistRound(
        int seasonId,
        RoundContext ctx,
        ICollection<PlayerRoundStats> playerStats
    )
    {
        using var db = new Db();
        var now = DateTime.UtcNow;
        var startingElo = Configs.GetConfigData().Elo.StartingElo;

        var round = new RoundRecord
        {
            SeasonId = seasonId,
            Map = ctx.Map,
            PlayedAtUtc = ctx.StartedAtUtc,
            RoundTypeOrdinal = (int) ctx.RoundType,
            IsRanked = ctx.IsRanked,
            TPlayerCount = ctx.TPlayerCount,
            CtPlayerCount = ctx.CtPlayerCount,
            WinnerTeamNum = ctx.WinnerTeamNum,
            BombSite = ctx.BombSite,
            BombPlanted = ctx.BombPlanted,
            BombDefused = ctx.BombDefused,
            BombExploded = ctx.BombExploded,
            RoundDurationSeconds = ctx.RoundDurationSeconds,
        };

        foreach (var s in playerStats)
        {
            round.PlayerRecords.Add(new PlayerRoundRecord
            {
                SeasonId = seasonId,
                Map = ctx.Map,
                PlayedAtUtc = ctx.StartedAtUtc,
                IsRanked = ctx.IsRanked,
                SteamId = s.SteamId,
                PlayerName = s.PlayerName,
                TeamNum = s.TeamNum,
                WonRound = s.WonRound,
                Kills = s.Kills,
                Headshots = s.Headshots,
                Assists = s.Assists,
                FlashAssists = s.FlashAssists,
                Damage = s.Damage,
                UtilityDamage = s.UtilityDamage,
                EnemiesFlashed = s.EnemiesFlashed,
                EnemyBlindDuration = s.EnemyBlindDuration,
                Died = s.Died,
                DiedAtSeconds = s.DiedAtSeconds,
                WasTeamKilled = s.WasTeamKilled,
                KilledTeammate = s.KilledTeammate,
                DiedEarly = s.DiedEarly,
                OpeningKill = s.OpeningKill,
                OpeningDeath = s.OpeningDeath,
                TradeKills = s.TradeKills,
                TradedDeath = s.TradedDeath,
                Kast = s.Kast,
                MultiKillCount = s.Kills,
                ClutchVersus = s.ClutchVersus,
                ClutchWon = s.ClutchWon,
                BombPlanted = s.Planted,
                BombDefused = s.Defused,
                WasAfk = s.WasAfk,
                Rating = s.Rating,
                EloDelta = s.EloDelta,
                EloAfter = s.EloAfter,
            });
        }

        db.RoundRecords.Add(round);

        var steamIds = playerStats.Select(s => s.SteamId).ToList();
        var existingStats = db.PlayerSeasonStats
            .Where(s => s.SeasonId == seasonId && steamIds.Contains(s.SteamId))
            .ToDictionary(s => s.SteamId, s => s);

        var result = new Dictionary<ulong, PlayerSeasonStats>();
        foreach (var s in playerStats)
        {
            if (s.SteamId == 0)
            {
                continue;
            }

            if (!existingStats.TryGetValue(s.SteamId, out var seasonStats))
            {
                seasonStats = new PlayerSeasonStats
                {
                    SeasonId = seasonId,
                    SteamId = s.SteamId,
                    Elo = startingElo,
                    PeakElo = startingElo,
                };
                db.PlayerSeasonStats.Add(seasonStats);
                existingStats[s.SteamId] = seasonStats;
            }
            else
            {
                db.Entry(seasonStats).State = EntityState.Modified;
            }

            if (ctx.IsRanked)
            {
                seasonStats.Elo = s.EloAfter;
                seasonStats.PeakElo = Math.Max(seasonStats.PeakElo, s.EloAfter);
                seasonStats.RankedRoundsPlayed++;
                if (s.WonRound)
                {
                    seasonStats.RankedRoundsWon++;
                }

                seasonStats.LastRankedRoundAtUtc = now;
            }
            else
            {
                seasonStats.UnrankedRoundsPlayed++;
            }

            seasonStats.UpdatedAtUtc = now;
            result[s.SteamId] = seasonStats;
        }

        db.SaveChanges();

        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            entry.State = EntityState.Detached;
        }

        return result;
    }

    #endregion

    #region Rankings and records

    /// <summary>
    /// Ladder placement of a player in a season (ranked players only).
    /// Null when the player has not played a ranked round in this season.
    /// </summary>
    public static PlacementInfo? GetPlacement(int seasonId, ulong steamId)
    {
        using var db = new Db();
        var mine = db.PlayerSeasonStats.AsNoTracking()
            .FirstOrDefault(s => s.SeasonId == seasonId && s.SteamId == steamId);
        if (mine is null || mine.RankedRoundsPlayed == 0)
        {
            return null;
        }

        var better = db.PlayerSeasonStats.AsNoTracking()
            .Count(s => s.SeasonId == seasonId && s.RankedRoundsPlayed > 0 && s.Elo > mine.Elo);
        var total = db.PlayerSeasonStats.AsNoTracking()
            .Count(s => s.SeasonId == seasonId && s.RankedRoundsPlayed > 0);

        return new PlacementInfo(better + 1, total, mine.Elo);
    }

    public static List<TopPlayerInfo> GetTopPlayers(int seasonId, int count)
    {
        using var db = new Db();
        return db.PlayerSeasonStats.AsNoTracking()
            .Where(s => s.SeasonId == seasonId && s.RankedRoundsPlayed > 0)
            .OrderByDescending(s => s.Elo)
            .Take(count)
            .Join(
                db.PlayerProfiles.AsNoTracking(),
                s => s.SteamId,
                p => p.SteamId,
                (s, p) => new TopPlayerInfo(s.SteamId, p.LastKnownName, s.Elo, s.RankedRoundsPlayed)
            )
            .ToList();
    }

    /// <summary>
    /// The best peak ELO reached in any previous (ended) season - the server record.
    /// </summary>
    public static SeasonBestInfo? GetPreviousSeasonsServerBest()
    {
        using var db = new Db();
        var best = db.PlayerSeasonStats.AsNoTracking()
            .Join(db.Seasons.AsNoTracking().Where(s => !s.IsActive),
                stats => stats.SeasonId, season => season.Id,
                (stats, season) => new {stats, season})
            .Where(x => x.stats.RankedRoundsPlayed > 0)
            .OrderByDescending(x => x.stats.PeakElo)
            .FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        var name = db.PlayerProfiles.AsNoTracking()
            .FirstOrDefault(p => p.SteamId == best.stats.SteamId)?.LastKnownName ?? "?";
        return new SeasonBestInfo(best.stats.SteamId, name, best.stats.PeakElo, best.season.Name);
    }

    /// <summary>
    /// A player's own best peak ELO from previous (ended) seasons.
    /// </summary>
    public static int? GetPreviousSeasonsPersonalBest(ulong steamId)
    {
        using var db = new Db();
        var peaks = db.PlayerSeasonStats.AsNoTracking()
            .Join(db.Seasons.AsNoTracking().Where(s => !s.IsActive),
                stats => stats.SeasonId, season => season.Id,
                (stats, season) => stats)
            .Where(s => s.SteamId == steamId && s.RankedRoundsPlayed > 0)
            .Select(s => (int?) s.PeakElo)
            .Max();
        return peaks;
    }

    #endregion

    #region Competitive Retakes (teams)

    public static CrTeamStats GetOrCreateCrTeam(int seasonId, string teamKey, string playerNames, int teamSize)
    {
        using var db = new Db();
        var team = db.CrTeamStats.AsNoTracking()
            .FirstOrDefault(t => t.SeasonId == seasonId && t.TeamKey == teamKey);
        if (team is not null)
        {
            return team;
        }

        team = new CrTeamStats
        {
            SeasonId = seasonId,
            TeamKey = teamKey,
            PlayerNames = playerNames,
            TeamSize = teamSize,
            Elo = Configs.GetConfigData().Competitive.TeamStartingElo,
            PeakElo = Configs.GetConfigData().Competitive.TeamStartingElo,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        db.CrTeamStats.Add(team);
        db.SaveChanges();
        db.Entry(team).State = EntityState.Detached;
        return team;
    }

    /// <summary>
    /// Persists a finished (or cancelled) CR match and applies the team ELO deltas.
    /// Returns the updated (teamA, teamB) rows.
    /// </summary>
    public static (CrTeamStats TeamA, CrTeamStats TeamB) PersistCrMatch(
        int seasonId,
        string map,
        DateTime startedAtUtc,
        string teamAKey, string teamAName,
        string teamBKey, string teamBName,
        int teamSize,
        int scoreA, int scoreB,
        string result,
        int eloDeltaA, int eloDeltaB
    )
    {
        // Ensure both rows exist first.
        GetOrCreateCrTeam(seasonId, teamAKey, teamAName, teamSize);
        GetOrCreateCrTeam(seasonId, teamBKey, teamBName, teamSize);

        using var db = new Db();
        var now = DateTime.UtcNow;

        var teamA = db.CrTeamStats.First(t => t.SeasonId == seasonId && t.TeamKey == teamAKey);
        var teamB = db.CrTeamStats.First(t => t.SeasonId == seasonId && t.TeamKey == teamBKey);

        var counted = result is "A" or "B" or "draw";
        if (counted)
        {
            teamA.MatchesPlayed++;
            teamB.MatchesPlayed++;
            if (result == "A")
            {
                teamA.MatchesWon++;
            }
            else if (result == "B")
            {
                teamB.MatchesWon++;
            }
            else
            {
                teamA.MatchesDrawn++;
                teamB.MatchesDrawn++;
            }

            teamA.RoundsWon += scoreA;
            teamA.RoundsLost += scoreB;
            teamB.RoundsWon += scoreB;
            teamB.RoundsLost += scoreA;

            teamA.Elo += eloDeltaA;
            teamB.Elo += eloDeltaB;
            teamA.PeakElo = Math.Max(teamA.PeakElo, teamA.Elo);
            teamB.PeakElo = Math.Max(teamB.PeakElo, teamB.Elo);
        }

        teamA.PlayerNames = teamAName;
        teamB.PlayerNames = teamBName;
        teamA.UpdatedAtUtc = now;
        teamB.UpdatedAtUtc = now;
        db.Entry(teamA).State = EntityState.Modified;
        db.Entry(teamB).State = EntityState.Modified;

        db.CrMatches.Add(new CrMatch
        {
            SeasonId = seasonId,
            Map = map,
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = now,
            TeamAKey = teamAKey,
            TeamBKey = teamBKey,
            TeamAName = teamAName,
            TeamBName = teamBName,
            TeamSize = teamSize,
            ScoreA = scoreA,
            ScoreB = scoreB,
            Result = result,
            EloDeltaA = counted ? eloDeltaA : 0,
            EloDeltaB = counted ? eloDeltaB : 0,
        });

        db.SaveChanges();
        return (teamA, teamB);
    }

    public static List<CrTeamStats> GetTopCrTeams(int seasonId, int count)
    {
        using var db = new Db();
        return db.CrTeamStats.AsNoTracking()
            .Where(t => t.SeasonId == seasonId && t.MatchesPlayed > 0)
            .OrderByDescending(t => t.Elo)
            .Take(count)
            .ToList();
    }

    public static async Task IncrementNemesisRecordAsync(ulong killerSteamId, ulong victimSteamId)
    {
        try
        {
            await using var db = new Db();
            var record = await db.NemesisRecords.FirstOrDefaultAsync(x => x.KillerSteamId == killerSteamId && x.VictimSteamId == victimSteamId);
            if (record is null)
            {
                record = new NemesisRecord { KillerSteamId = killerSteamId, VictimSteamId = victimSteamId, Kills = 0 };
                db.NemesisRecords.Add(record);
            }

            record.Kills++;
            await db.SaveChangesAsync();
        }
        catch (Exception e)
        {
            Log.Error($"Failed to increment nemesis record: {e.Message}");
        }
    }

    #endregion

    #region Stats summaries

    public static PlayerSeasonSummary GetPlayerSeasonSummary(int seasonId, ulong steamId, bool rankedOnly = false)
    {
        using var db = new Db();
        var rows = db.PlayerRoundRecords.AsNoTracking()
            .Where(r => r.SeasonId == seasonId && r.SteamId == steamId);
        if (rankedOnly)
        {
            rows = rows.Where(r => r.IsRanked);
        }

        var list = rows.ToList();
        var seasonStats = GetSeasonStats(seasonId, steamId);

        if (list.Count == 0)
        {
            var startingElo = Configs.GetConfigData().Elo.StartingElo;
            return new PlayerSeasonSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                seasonStats?.Elo ?? startingElo, seasonStats?.PeakElo ?? startingElo);
        }

        return new PlayerSeasonSummary(
            RoundsPlayed: list.Count,
            RankedRoundsPlayed: list.Count(r => r.IsRanked),
            Kills: list.Sum(r => r.Kills),
            Deaths: list.Count(r => r.Died),
            Assists: list.Sum(r => r.Assists),
            Headshots: list.Sum(r => r.Headshots),
            AverageDamagePerRound: list.Average(r => (double) r.Damage),
            KastPercent: 100.0 * list.Count(r => r.Kast) / list.Count,
            AverageRating: list.Where(r => !r.WasAfk).Select(r => r.Rating).DefaultIfEmpty(0).Average(),
            ClutchWins: list.Count(r => r.ClutchWon),
            OpeningKills: list.Count(r => r.OpeningKill),
            BombPlants: list.Count(r => r.BombPlanted),
            BombDefuses: list.Count(r => r.BombDefused),
            Elo: seasonStats?.Elo ?? Configs.GetConfigData().Elo.StartingElo,
            PeakElo: seasonStats?.PeakElo ?? Configs.GetConfigData().Elo.StartingElo
        );
    }

    #endregion

    #region Garden admins (ROADMAP R3)

    public static List<GardenAdmin> GetGardenAdmins()
    {
        using var db = new Db();
        return db.GardenAdmins.ToList();
    }

    public static void UpsertGardenAdmin(ulong steamId, string name, int level, ulong addedBy)
    {
        using var db = new Db();
        var existing = db.GardenAdmins.AsTracking().FirstOrDefault(a => a.SteamId == steamId);
        if (existing is null)
        {
            db.GardenAdmins.Add(new GardenAdmin
            {
                SteamId = steamId,
                Name = name,
                Level = level,
                AddedBy = addedBy,
                AddedAtUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Name = name;
            existing.Level = level;
            existing.AddedBy = addedBy;
        }

        db.SaveChanges();
    }

    public static void DeleteGardenAdmin(ulong steamId)
    {
        using var db = new Db();
        db.GardenAdmins.Where(a => a.SteamId == steamId).ExecuteDelete();
    }

    /// <summary>Garden Duels: persists one completed 1v1.</summary>
    public static void PersistDuel(
        int seasonId, string map, string arenaName,
        ulong winnerSteamId, string winnerName,
        ulong loserSteamId, string loserName,
        bool isChallenge, string challengeScore)
    {
        using var db = new Db();
        db.DuelRecords.Add(new DuelRecord
        {
            SeasonId = seasonId,
            Map = map,
            PlayedAtUtc = DateTime.UtcNow,
            ArenaName = arenaName,
            WinnerSteamId = winnerSteamId,
            WinnerName = winnerName,
            LoserSteamId = loserSteamId,
            LoserName = loserName,
            IsChallenge = isChallenge,
            ChallengeScore = challengeScore,
        });
        db.SaveChanges();
    }

    public static void LogGardenAdminAction(
        ulong actorSteamId, string actorName, string action,
        ulong targetSteamId, string targetName, string detail)
    {
        using var db = new Db();
        db.GardenAdminLog.Add(new GardenAdminLogEntry
        {
            AtUtc = DateTime.UtcNow,
            ActorSteamId = actorSteamId,
            ActorName = actorName,
            Action = action,
            TargetSteamId = targetSteamId,
            TargetName = targetName,
            Detail = detail,
        });
        db.SaveChanges();
    }

    #endregion
}
