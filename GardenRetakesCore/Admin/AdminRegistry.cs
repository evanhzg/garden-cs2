using System.Text.Json;
using System.Text.Json.Serialization;

namespace GardenRetakes.Core.Admin;

public enum AdminLevel
{
    None = 0,
    Moderator = 1,
    Admin = 2,
    Owner = 3,
}

public class AdminEntry
{
    [JsonPropertyName("steamId")]
    public ulong SteamId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("level")]
    public AdminLevel Level { get; set; } = AdminLevel.Moderator;

    [JsonPropertyName("addedBy")]
    public ulong AddedBy { get; set; }

    [JsonPropertyName("addedAtUtc")]
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Garden admin registry — pure logic + JSON (de)serialization. Persistence
/// location is the caller's problem (plugin stores it as garden_admins.json in
/// the module directory today; DB-backed storage can reuse the same model later).
///
/// Level policy (see ROADMAP R3):
///   Moderator — kick, map change
///   Admin     — + slay, config commands, admin list
///   Owner     — + rcon, add/remove admins
/// </summary>
public class AdminRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<ulong, AdminEntry> _admins = new();

    /// <summary>Owners defined in the config file — always Owner, cannot be removed.</summary>
    private readonly HashSet<ulong> _configOwners = new();

    public IReadOnlyCollection<AdminEntry> All =>
        _admins.Values.OrderByDescending(a => a.Level).ThenBy(a => a.Name).ToList();

    public void BootstrapConfigOwners(IEnumerable<ulong> steamIds)
    {
        foreach (var steamId in steamIds.Where(s => s != 0))
        {
            _configOwners.Add(steamId);
            if (!_admins.TryGetValue(steamId, out var existing) || existing.Level < AdminLevel.Owner)
            {
                _admins[steamId] = new AdminEntry
                {
                    SteamId = steamId,
                    Name = existing?.Name ?? "config-owner",
                    Level = AdminLevel.Owner,
                    AddedBy = 0,
                };
            }
        }
    }

    public AdminLevel GetLevel(ulong steamId) =>
        _admins.TryGetValue(steamId, out var entry) ? entry.Level : AdminLevel.None;

    public bool HasLevel(ulong steamId, AdminLevel required) => GetLevel(steamId) >= required;

    /// <summary>
    /// Add or update an admin. Only Owners may grant. The caller resolves the
    /// actor's effective level (console / @css/root count as Owner there).
    /// </summary>
    public bool TryAdd(ulong actorId, AdminLevel actorLevel, ulong target, string targetName, AdminLevel level, out string? error)
    {
        error = null;
        if (level is AdminLevel.None)
        {
            error = "invalid_level";
            return false;
        }

        if (actorLevel < AdminLevel.Owner)
        {
            error = "not_owner";
            return false;
        }

        _admins[target] = new AdminEntry
        {
            SteamId = target,
            Name = targetName,
            Level = level,
            AddedBy = actorId,
        };
        return true;
    }

    public bool TryRemove(AdminLevel actorLevel, ulong target, out string? error)
    {
        error = null;
        if (actorLevel < AdminLevel.Owner)
        {
            error = "not_owner";
            return false;
        }

        if (_configOwners.Contains(target))
        {
            error = "config_owner";
            return false;
        }

        if (!_admins.Remove(target))
        {
            error = "not_found";
            return false;
        }

        return true;
    }

    // ---------- persistence ----------

    public string Serialize() =>
        JsonSerializer.Serialize(_admins.Values.Where(a => !_configOwners.Contains(a.SteamId)), JsonOptions);

    public void Load(string json)
    {
        Import(JsonSerializer.Deserialize<List<AdminEntry>>(json, JsonOptions) ?? []);
    }

    /// <summary>Merges entries from any persistence source (JSON file, database, ...).</summary>
    public void Import(IEnumerable<AdminEntry> entries)
    {
        foreach (var entry in entries.Where(e => e.SteamId != 0 && e.Level != AdminLevel.None))
        {
            // Config owners keep their Owner level regardless of stored contents.
            if (!_configOwners.Contains(entry.SteamId))
            {
                _admins[entry.SteamId] = entry;
            }
        }
    }
}
