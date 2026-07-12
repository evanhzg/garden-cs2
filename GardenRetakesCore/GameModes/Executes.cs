using System.Text.Json;
using System.Text.Json.Serialization;

namespace GardenRetakes.Core.GameModes;

/// <summary>
/// Data model for Executes mode (ROADMAP R6) and Fast-strat (R7, same format).
/// Pure logic + JSON, no CounterStrikeSharp dependency (unit tested).
/// Stored per map as executes/&lt;map&gt;.json in the plugin directory.
/// </summary>
public class ExecutePosition
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }
}

public enum UtilityType
{
    Smoke,
    Flash,
    HE,
    Molotov,
}

/// <summary>A recorded grenade throw: spawn point + initial velocity of the projectile.</summary>
public class UtilityThrow
{
    public UtilityType Type { get; set; }

    /// <summary>"T" or "CT" — which side this lineup belongs to (R10; old JSONs default to T).</summary>
    public string Team { get; set; } = "T";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float VelX { get; set; }
    public float VelY { get; set; }
    public float VelZ { get; set; }

    /// <summary>Seconds after round start before this nade is thrown.</summary>
    public float DelaySeconds { get; set; }
}

/// <summary>
/// One named strategy: where the Ts start their execute, where the CTs set up,
/// and which utility flies automatically.
/// </summary>
public class ExecuteStrategy
{
    public string Name { get; set; } = "";

    /// <summary>"A" or "B".</summary>
    public string Site { get; set; } = "A";

    public string? AddedBy { get; set; }

    /// <summary>
    /// Random-selection weight (R10). 1 = normal, higher = more frequent,
    /// 0 = never picked randomly (still playable via !gexec play).
    /// </summary>
    public int Weight { get; set; } = 1;

    public List<ExecutePosition> TStarts { get; set; } = [];
    public List<ExecutePosition> CtSetups { get; set; } = [];
    public List<UtilityThrow> Utilities { get; set; } = [];

    /// <summary>Playable when both sides have at least one position.</summary>
    [JsonIgnore]
    public bool IsPlayable => TStarts.Count > 0 && CtSetups.Count > 0;
}

public class ExecuteMapData
{
    public List<ExecuteStrategy> Strategies { get; set; } = [];
}

/// <summary>In-memory store with (de)serialization and selection logic.</summary>
public class ExecuteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private ExecuteMapData _data = new();

    public IReadOnlyList<ExecuteStrategy> Strategies => _data.Strategies;

    public IReadOnlyList<ExecuteStrategy> Playable =>
        _data.Strategies.Where(s => s.IsPlayable).ToList();

    public ExecuteStrategy? Find(string name) =>
        _data.Strategies.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates a strategy; fails on duplicate names or invalid site.</summary>
    public bool TryAdd(string name, string site, string? addedBy, out string? error)
    {
        error = null;
        site = site.ToUpperInvariant();
        name = name.Trim();
        // R11: multi-word names allowed.
        if (string.IsNullOrWhiteSpace(name) || name.Length > 48)
        {
            error = "bad_name";
            return false;
        }

        if (site is not ("A" or "B"))
        {
            error = "bad_site";
            return false;
        }

        if (Find(name) is not null)
        {
            error = "duplicate";
            return false;
        }

        _data.Strategies.Add(new ExecuteStrategy { Name = name, Site = site, AddedBy = addedBy });
        return true;
    }

    public bool Remove(string name)
    {
        var strategy = Find(name);
        return strategy is not null && _data.Strategies.Remove(strategy);
    }

    /// <summary>Weighted random pick (R10): Weight 0 strategies are excluded.</summary>
    public ExecuteStrategy? PickRandom(Random random, string? site = null)
    {
        var pool = Playable
            .Where(s => s.Weight > 0 &&
                        (site is null || s.Site.Equals(site, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (pool.Count == 0)
        {
            return null;
        }

        var total = pool.Sum(s => s.Weight);
        var roll = random.Next(total);
        foreach (var strategy in pool)
        {
            roll -= strategy.Weight;
            if (roll < 0)
            {
                return strategy;
            }
        }

        return pool[^1];
    }

    public string Serialize() => JsonSerializer.Serialize(_data, JsonOptions);

    public void Load(string json) =>
        _data = JsonSerializer.Deserialize<ExecuteMapData>(json, JsonOptions) ?? new ExecuteMapData();

    public void Clear() => _data = new ExecuteMapData();
}
