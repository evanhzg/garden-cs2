using GardenRankingsCore.Config;

namespace GardenRankings;

/// <summary>
/// Detects players who spend an entire live round without moving or pressing anything.
/// After a configurable number of consecutive AFK rounds the plugin moves them to
/// spectators. AFK rounds never gain or lose ELO.
/// </summary>
public class AfkTracker
{
    private class TrackedPlayer
    {
        public float StartX;
        public float StartY;
        public float StartZ;
        public bool HasStartPosition;
        public bool Moved;
    }

    private readonly Dictionary<ulong, TrackedPlayer> _roundTracking = new();
    private readonly Dictionary<ulong, int> _consecutiveAfkRounds = new();

    public void BeginRound(IEnumerable<ulong> steamIds)
    {
        _roundTracking.Clear();
        foreach (var steamId in steamIds)
        {
            _roundTracking[steamId] = new TrackedPlayer();
        }
    }

    /// <summary>
    /// Called periodically during the live round for every tracked alive player.
    /// </summary>
    public void Sample(ulong steamId, float x, float y, float z, ulong buttons)
    {
        if (!_roundTracking.TryGetValue(steamId, out var tracked) || tracked.Moved)
        {
            return;
        }

        if (!tracked.HasStartPosition)
        {
            tracked.StartX = x;
            tracked.StartY = y;
            tracked.StartZ = z;
            tracked.HasStartPosition = true;
            return;
        }

        if (buttons != 0)
        {
            tracked.Moved = true;
            return;
        }

        var threshold = Configs.GetConfigData().Afk.MoveThresholdUnits;
        var dx = x - tracked.StartX;
        var dy = y - tracked.StartY;
        var dz = z - tracked.StartZ;
        if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > threshold)
        {
            tracked.Moved = true;
        }
    }

    public bool WasAfkThisRound(ulong steamId)
    {
        return _roundTracking.TryGetValue(steamId, out var tracked) && tracked is {Moved: false, HasStartPosition: true};
    }

    /// <summary>
    /// Updates consecutive AFK counters at round end. Returns players who reached the
    /// spectate threshold (their counter is reset).
    /// </summary>
    public List<ulong> OnRoundEnd(ICollection<ulong> afkPlayers, ICollection<ulong> activePlayers)
    {
        var toSpectate = new List<ulong>();
        var limit = Configs.GetConfigData().Afk.RoundsBeforeSpectate;

        foreach (var steamId in afkPlayers)
        {
            var count = _consecutiveAfkRounds.TryGetValue(steamId, out var c) ? c + 1 : 1;
            if (count >= limit)
            {
                toSpectate.Add(steamId);
                _consecutiveAfkRounds.Remove(steamId);
            }
            else
            {
                _consecutiveAfkRounds[steamId] = count;
            }
        }

        foreach (var steamId in activePlayers)
        {
            _consecutiveAfkRounds.Remove(steamId);
        }

        return toSpectate;
    }

    public void ForgetPlayer(ulong steamId)
    {
        _consecutiveAfkRounds.Remove(steamId);
        _roundTracking.Remove(steamId);
    }
}
