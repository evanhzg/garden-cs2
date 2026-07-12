namespace GardenRetakes.Core.GameModes;

/// <summary>
/// Pure logic for Duels mode (ROADMAP R4) — no CounterStrikeSharp dependency.
/// </summary>
public static class DuelArenas
{
    public record ArenaSpawn(int Id, double X, double Y, double Z);

    public record Arena(int SpawnIdA, int SpawnIdB);

    /// <summary>
    /// Builds 1v1 arenas by greedily pairing the closest duel-flagged spawns
    /// (both endpoints unused, distance ≤ maxPairDistance). Team of the spawn is
    /// irrelevant — duelists get teleported to either end.
    /// </summary>
    public static List<Arena> BuildArenas(IReadOnlyList<ArenaSpawn> spawns, double maxPairDistance)
    {
        var candidates = new List<(double Distance, int A, int B)>();
        for (var i = 0; i < spawns.Count; i++)
        {
            for (var j = i + 1; j < spawns.Count; j++)
            {
                var dx = spawns[i].X - spawns[j].X;
                var dy = spawns[i].Y - spawns[j].Y;
                var dz = spawns[i].Z - spawns[j].Z;
                var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (distance <= maxPairDistance)
                {
                    candidates.Add((distance, spawns[i].Id, spawns[j].Id));
                }
            }
        }

        var used = new HashSet<int>();
        var arenas = new List<Arena>();
        foreach (var (_, a, b) in candidates.OrderBy(c => c.Distance))
        {
            if (used.Contains(a) || used.Contains(b))
            {
                continue;
            }

            used.Add(a);
            used.Add(b);
            arenas.Add(new Arena(a, b));
        }

        return arenas;
    }
}

/// <summary>
/// Duel rotation state machine: two fighters, a FIFO queue for everyone else,
/// win counters. On a death the loser goes to the back of the queue (when
/// someone is waiting) and the next player steps in.
/// </summary>
public class DuelSession
{
    private readonly List<ulong> _queue = [];

    public ulong? FighterA { get; private set; }
    public ulong? FighterB { get; private set; }
    public Dictionary<ulong, int> Wins { get; } = new();

    public IReadOnlyList<ulong> Queue => _queue;

    public bool HasActiveDuel => FighterA is not null && FighterB is not null;

    public bool IsFighter(ulong id) => FighterA == id || FighterB == id;

    /// <summary>Adds a player; returns true when they immediately become a fighter.</summary>
    public bool AddPlayer(ulong id)
    {
        if (IsFighter(id) || _queue.Contains(id))
        {
            return false;
        }

        Wins.TryAdd(id, 0);

        if (FighterA is null)
        {
            FighterA = id;
            return true;
        }

        if (FighterB is null)
        {
            FighterB = id;
            return true;
        }

        _queue.Add(id);
        return false;
    }

    /// <summary>Removes a player (disconnect/spectate). Returns the promoted fighter, if any.</summary>
    public ulong? RemovePlayer(ulong id)
    {
        _queue.Remove(id);

        if (FighterA == id)
        {
            FighterA = null;
        }
        else if (FighterB == id)
        {
            FighterB = null;
        }
        else
        {
            return null;
        }

        return PromoteFromQueue();
    }

    private ulong? PromoteFromQueue()
    {
        if (_queue.Count == 0)
        {
            return null;
        }

        var next = _queue[0];
        _queue.RemoveAt(0);
        if (FighterA is null)
        {
            FighterA = next;
        }
        else
        {
            FighterB = next;
        }

        return next;
    }

    public record DuelOutcome(ulong WinnerId, ulong LoserId, ulong NextA, ulong NextB, bool LoserRotatedOut);

    /// <summary>
    /// Registers a death of one of the fighters. The winner keeps fighting; with
    /// players waiting, the loser rotates to the back of the queue.
    /// </summary>
    public DuelOutcome? OnFighterDeath(ulong deadId)
    {
        if (!HasActiveDuel || !IsFighter(deadId))
        {
            return null;
        }

        var winner = FighterA == deadId ? FighterB!.Value : FighterA!.Value;
        Wins[winner] = Wins.TryGetValue(winner, out var score) ? score + 1 : 1;

        var rotated = false;
        if (_queue.Count > 0)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            _queue.Add(deadId);
            FighterA = winner;
            FighterB = next;
            rotated = true;
        }
        else
        {
            FighterA = winner;
            FighterB = deadId;
        }

        return new DuelOutcome(winner, deadId, FighterA.Value, FighterB.Value, rotated);
    }

    public void Reset()
    {
        _queue.Clear();
        FighterA = null;
        FighterB = null;
        Wins.Clear();
    }
}
