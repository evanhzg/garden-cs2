using GardenRankingsCore.Config;
using Microsoft.EntityFrameworkCore;

namespace GardenRankingsCore.Db;

public class Db : DbContext
{
    public DbSet<Season> Seasons { get; set; }
    public DbSet<PlayerProfile> PlayerProfiles { get; set; }
    public DbSet<PlayerSeasonStats> PlayerSeasonStats { get; set; }
    public DbSet<RoundRecord> RoundRecords { get; set; }
    public DbSet<PlayerRoundRecord> PlayerRoundRecords { get; set; }
    public DbSet<CrTeamStats> CrTeamStats { get; set; }
    public DbSet<CrMatch> CrMatches { get; set; }
    public DbSet<GardenAdmin> GardenAdmins { get; set; }
    public DbSet<GardenAdminLogEntry> GardenAdminLog { get; set; }
    public DbSet<DuelRecord> DuelRecords { get; set; }
    public DbSet<GardenBan> GardenBans { get; set; }
    public DbSet<GardenNameOverride> GardenNameOverrides { get; set; }
    public DbSet<WebLiveMatch> WebLiveMatches { get; set; }

    private static Db? Instance { get; set; }

    public static Db GetInstance()
    {
        return Instance ??= new Db();
    }

    public static void Disconnect()
    {
        Instance?.Dispose();
        Instance = null;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

        var configData = Configs.IsLoaded() ? Configs.GetConfigData() : new ConfigData();
        var connectionString = configData.DatabaseConnectionString;
        switch (configData.DatabaseProvider)
        {
            case DatabaseProvider.Sqlite:
                optionsBuilder.UseSqlite(connectionString);
                break;
            case DatabaseProvider.MySql:
                optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlayerSeasonStats>()
            .HasIndex(s => new {s.SeasonId, s.SteamId})
            .IsUnique();

        modelBuilder.Entity<PlayerRoundRecord>()
            .HasIndex(r => new {r.SteamId, r.PlayedAtUtc});

        modelBuilder.Entity<PlayerRoundRecord>()
            .HasIndex(r => new {r.SeasonId, r.SteamId});

        modelBuilder.Entity<RoundRecord>()
            .HasIndex(r => r.PlayedAtUtc);

        modelBuilder.Entity<CrTeamStats>()
            .HasIndex(t => new {t.SeasonId, t.TeamKey})
            .IsUnique();

        modelBuilder.Entity<CrMatch>()
            .HasIndex(m => m.SeasonId);

        base.OnModelCreating(modelBuilder);
    }
}
