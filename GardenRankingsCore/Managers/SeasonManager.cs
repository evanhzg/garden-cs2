using GardenRankingsCore.Config;
using GardenRankingsCore.Db;

namespace GardenRankingsCore.Managers;

public record RecordAnnouncement(
    bool ServerRecordBroken,
    bool PersonalBestBroken,
    int NewElo,
    int? PreviousServerRecord,
    string? PreviousServerRecordHolder,
    int? PreviousPersonalBest
);

/// <summary>
/// Caches the active season and the "previous seasons" records so record checks
/// are free at round end. All heavy reads happen at initialization / on join.
/// </summary>
public class SeasonManager
{
    private static SeasonManager? _instance;
    public static SeasonManager Instance => _instance ??= new SeasonManager();

    private readonly object _lock = new();

    private Season? _activeSeason;

    // Server record from previous seasons; raised each time it gets beaten so
    // the announcement only fires on genuine improvements.
    private int? _serverRecordThreshold;
    private string? _serverRecordHolder;

    // Personal previous-season bests, cached per player, raised when beaten.
    private readonly Dictionary<ulong, int?> _personalBestThresholds = new();

    public int ActiveSeasonId => _activeSeason?.Id ?? 0;
    public string ActiveSeasonName => _activeSeason?.Name ?? "?";
    public DateTime ActiveSeasonStartedAtUtc => _activeSeason?.StartedAtUtc ?? DateTime.UtcNow;

    public void Initialize()
    {
        _activeSeason = Queries.EnsureActiveSeason();
        ReloadRecords();
    }

    public void ReloadRecords()
    {
        var best = Queries.GetPreviousSeasonsServerBest();
        lock (_lock)
        {
            _serverRecordThreshold = best?.PeakElo;
            _serverRecordHolder = best?.Name;
            _personalBestThresholds.Clear();
        }
    }

    public Season StartNewSeason(string? name = null)
    {
        _activeSeason = Queries.StartNewSeason(name);
        ReloadRecords();
        return _activeSeason;
    }

    /// <summary>
    /// Starts a new season automatically when the configured rotation period elapsed.
    /// Returns the new season, or null when no rotation happened.
    /// </summary>
    public Season? CheckAutoRotate(DateTime utcNow)
    {
        var days = Configs.GetConfigData().Season.AutoRotateDays;
        if (days <= 0 || _activeSeason is null)
        {
            return null;
        }

        if (utcNow < _activeSeason.StartedAtUtc.AddDays(days))
        {
            return null;
        }

        return StartNewSeason();
    }

    /// <summary>
    /// Preload a player's previous-season personal best (call on join, off the game thread).
    /// </summary>
    public void PreloadPersonalBest(ulong steamId)
    {
        lock (_lock)
        {
            if (_personalBestThresholds.ContainsKey(steamId))
            {
                return;
            }
        }

        var best = Queries.GetPreviousSeasonsPersonalBest(steamId);

        lock (_lock)
        {
            _personalBestThresholds.TryAdd(steamId, best);
        }
    }

    /// <summary>
    /// Checks a fresh ELO value against previous-season records. Thresholds are
    /// raised after each break so chat is only prompted on real improvements.
    /// </summary>
    public RecordAnnouncement CheckRecords(ulong steamId, int newElo)
    {
        lock (_lock)
        {
            return CheckRecordsInternal(steamId, newElo);
        }
    }

    private RecordAnnouncement CheckRecordsInternal(ulong steamId, int newElo)
    {
        var serverBroken = false;
        int? previousServerRecord = null;
        string? previousHolder = null;

        if (_serverRecordThreshold is not null && newElo > _serverRecordThreshold)
        {
            serverBroken = true;
            previousServerRecord = _serverRecordThreshold;
            previousHolder = _serverRecordHolder;
            _serverRecordThreshold = newElo;
            _serverRecordHolder = null; // now held by the current player this season
        }

        var personalBroken = false;
        int? previousPersonalBest = null;

        if (_personalBestThresholds.TryGetValue(steamId, out var personalBest) &&
            personalBest is not null && newElo > personalBest)
        {
            personalBroken = true;
            previousPersonalBest = personalBest;
            _personalBestThresholds[steamId] = newElo;
        }

        return new RecordAnnouncement(
            serverBroken, personalBroken, newElo, previousServerRecord, previousHolder, previousPersonalBest);
    }
}
