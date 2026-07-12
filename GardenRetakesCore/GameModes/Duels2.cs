using System.Text.Json;
using System.Text.Json.Serialization;

namespace GardenRetakes.Core.GameModes;

/// <summary>
/// Duels v2 (ROADMAP R8) — named arenas, parallel lanes and challenges.
/// Pure logic, no CounterStrikeSharp dependency (unit tested).
/// </summary>
public class NamedDuelArena
{
    public string Name { get; set; } = "";
    public string? AddedBy { get; set; }
    public ExecutePosition? EndA { get; set; }
    public ExecutePosition? EndB { get; set; }

    [JsonIgnore]
    public bool IsComplete => EndA is not null && EndB is not null;
}

public class DuelMapData
{
    public List<NamedDuelArena> Arenas { get; set; } = [];
}

/// <summary>Named arena pairs, stored per map as duels/&lt;map&gt;.json.</summary>
public class DuelArenaStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private DuelMapData _data = new();

    public IReadOnlyList<NamedDuelArena> Arenas => _data.Arenas;

    public IReadOnlyList<NamedDuelArena> Complete =>
        _data.Arenas.Where(a => a.IsComplete).ToList();

    public NamedDuelArena? Find(string name) =>
        _data.Arenas.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool TryAdd(string name, string? addedBy, out string? error)
    {
        error = null;
        name = name.Trim();
        // R11: multi-word names allowed ("A Site VS Long").
        if (string.IsNullOrWhiteSpace(name) || name.Length > 48)
        {
            error = "bad_name";
            return false;
        }

        if (Find(name) is not null)
        {
            error = "duplicate";
            return false;
        }

        _data.Arenas.Add(new NamedDuelArena { Name = name.Trim(), AddedBy = addedBy });
        return true;
    }

    public bool Remove(string name)
    {
        var arena = Find(name);
        return arena is not null && _data.Arenas.Remove(arena);
    }

    public string Serialize() => JsonSerializer.Serialize(_data, JsonOptions);

    public void Load(string json) =>
        _data = JsonSerializer.Deserialize<DuelMapData>(json, JsonOptions) ?? new DuelMapData();

    public void Clear() => _data = new DuelMapData();
}

/// <summary>One 1v1 lane. Challenge lanes never rotate with the queue.</summary>
public class DuelLane
{
    public int Id { get; init; }
    public ulong? FighterA { get; set; }
    public ulong? FighterB { get; set; }

    public bool IsChallenge { get; init; }

    /// <summary>Challenge target score; null = infinite (until cancelled).</summary>
    public int? FirstTo { get; init; }

    public int ScoreA { get; set; }
    public int ScoreB { get; set; }

    public bool IsReady => FighterA is not null && FighterB is not null;

    public bool Contains(ulong id) => FighterA == id || FighterB == id;

    public string ScoreLine => $"{ScoreA}-{ScoreB}";
}

/// <summary>
/// Multi-lane duel state machine: normal lanes share one FIFO queue (loser
/// rotates out when someone waits); challenge lanes are reserved for their two
/// players with their own score and optional first-to-X finish.
/// </summary>
public class DuelManager
{
    private readonly List<DuelLane> _lanes = [];
    private readonly List<ulong> _queue = [];
    private int _nextLaneId;

    /// <summary>Cap on concurrently running lanes (set from arena count + config).</summary>
    public int MaxLanes { get; set; } = 1;

    public Dictionary<ulong, int> Wins { get; } = new();

    public IReadOnlyList<DuelLane> Lanes => _lanes;
    public IReadOnlyList<ulong> Queue => _queue;

    public DuelLane? LaneOf(ulong id) => _lanes.FirstOrDefault(l => l.Contains(id));

    public bool IsKnown(ulong id) => LaneOf(id) is not null || _queue.Contains(id);

    /// <summary>Adds a player to the first free slot, a new lane, or the queue.</summary>
    public DuelLane? AddPlayer(ulong id)
    {
        if (IsKnown(id))
        {
            return LaneOf(id);
        }

        Wins.TryAdd(id, 0);

        // Fill an existing normal lane with a free slot first.
        var open = _lanes.FirstOrDefault(l => !l.IsChallenge && !l.IsReady);
        if (open is not null)
        {
            if (open.FighterA is null) open.FighterA = id;
            else open.FighterB = id;
            return open;
        }

        // Room for a new lane?
        if (_lanes.Count < MaxLanes)
        {
            var lane = new DuelLane { Id = _nextLaneId++ };
            lane.FighterA = id;
            if (_queue.Count > 0)
            {
                lane.FighterB = _queue[0];
                _queue.RemoveAt(0);
            }

            _lanes.Add(lane);
            return lane;
        }

        _queue.Add(id);
        return null;
    }

    /// <summary>Removes a player entirely (disconnect). The lane partner is re-slotted.</summary>
    public void RemovePlayer(ulong id)
    {
        _queue.Remove(id);

        var lane = LaneOf(id);
        if (lane is null)
        {
            return;
        }

        var partner = lane.FighterA == id ? lane.FighterB : lane.FighterA;
        _lanes.Remove(lane);
        if (partner is not null)
        {
            AddPlayer(partner.Value);
        }
    }

    public record DuelResult(DuelLane Lane, ulong WinnerId, ulong LoserId,
        bool ChallengeFinished, bool LoserRotatedOut);

    /// <summary>Registers a fighter death; returns null when the dead player wasn't fighting.</summary>
    public DuelResult? OnDeath(ulong deadId)
    {
        var lane = LaneOf(deadId);
        if (lane is null || !lane.IsReady)
        {
            return null;
        }

        var winner = lane.FighterA == deadId ? lane.FighterB!.Value : lane.FighterA!.Value;
        Wins[winner] = Wins.GetValueOrDefault(winner) + 1;

        if (lane.IsChallenge)
        {
            if (lane.FighterA == winner) lane.ScoreA++;
            else lane.ScoreB++;

            var finished = lane.FirstTo is { } target &&
                           Math.Max(lane.ScoreA, lane.ScoreB) >= target;
            if (finished)
            {
                // Challenge over: dissolve the lane, both players rejoin the pool.
                _lanes.Remove(lane);
                AddPlayer(winner);
                AddPlayer(deadId);
            }

            return new DuelResult(lane, winner, deadId, finished, false);
        }

        var rotated = false;
        if (_queue.Count > 0)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            _queue.Add(deadId);
            lane.FighterA = winner;
            lane.FighterB = next;
            rotated = true;
        }

        return new DuelResult(lane, winner, deadId, false, rotated);
    }

    /// <summary>
    /// Reserves a lane for two players (no rotation). They are extracted from
    /// their current lanes/queue; abandoned partners are re-slotted.
    /// </summary>
    public DuelLane StartChallenge(ulong a, ulong b, int? firstTo)
    {
        Extract(a);
        Extract(b);
        Wins.TryAdd(a, 0);
        Wins.TryAdd(b, 0);

        var lane = new DuelLane
        {
            Id = _nextLaneId++,
            IsChallenge = true,
            FirstTo = firstTo,
            FighterA = a,
            FighterB = b,
        };
        _lanes.Add(lane);
        return lane;
    }

    /// <summary>Cancels a challenge lane; both players rejoin the pool.</summary>
    public bool CancelChallenge(ulong participant)
    {
        var lane = LaneOf(participant);
        if (lane is null || !lane.IsChallenge)
        {
            return false;
        }

        var a = lane.FighterA;
        var b = lane.FighterB;
        _lanes.Remove(lane);
        if (a is not null) AddPlayer(a.Value);
        if (b is not null) AddPlayer(b.Value);
        return true;
    }

    private void Extract(ulong id)
    {
        _queue.Remove(id);
        var lane = LaneOf(id);
        if (lane is null)
        {
            return;
        }

        var partner = lane.FighterA == id ? lane.FighterB : lane.FighterA;
        _lanes.Remove(lane);
        if (partner is not null)
        {
            AddPlayer(partner.Value);
        }
    }

    public void Reset()
    {
        _lanes.Clear();
        _queue.Clear();
        Wins.Clear();
        _nextLaneId = 0;
    }
}
