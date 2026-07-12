using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace RetakesAllocatorCore.Config;

public static class Configs
{
    public static class Shared
    {
        public static string? Module { get; set; }
    }
    private static readonly string ConfigDirectoryName = "config";
    private static readonly string ConfigFileName = "config.json";

    private static string? _configFilePath;
    private static ConfigData? _configData;

    private static readonly JsonSerializerOptions SerializationOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter()
        },
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static bool IsLoaded()
    {
        return _configData is not null;
    }

    public static ConfigData GetConfigData()
    {
        if (_configData is null)
        {
            throw new Exception("Config not yet loaded.");
        }

        return _configData;
    }

    public static ConfigData Load(string modulePath, bool saveAfterLoad = false)
    {
        var configFileDirectory = Path.Combine(modulePath, ConfigDirectoryName);
        Directory.CreateDirectory(configFileDirectory);

        _configFilePath = Path.Combine(configFileDirectory, ConfigFileName);
        if (File.Exists(_configFilePath))
        {
            _configData =
                JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(_configFilePath), SerializationOptions);
        }
        else
        {
            _configData = new ConfigData();
        }

        if (_configData is null)
        {
            throw new Exception("Failed to load configs.");
        }

        if (saveAfterLoad)
        {
            SaveConfigData(_configData);
        }

        _configData.Validate();

        return _configData;
    }

    public static ConfigData OverrideConfigDataForTests(
        ConfigData configData
    )
    {
        configData.Validate();
        _configData = configData;
        return _configData;
    }

    private static void SaveConfigData(ConfigData configData)
    {
        if (_configFilePath is null)
        {
            throw new Exception("Config not yet loaded.");
        }

        File.WriteAllText(_configFilePath, JsonSerializer.Serialize(configData, SerializationOptions));
    }

    /// <summary>Garden (ROADMAP R2): persists the current in-memory config (in-game !gconfig edits).</summary>
    public static void Save()
    {
        if (_configData is not null)
        {
            SaveConfigData(_configData);
        }
    }

    public static string? StringifyConfig(string? configName)
    {
        var configData = GetConfigData();
        if (configName is null)
        {
            return JsonSerializer.Serialize(configData, SerializationOptions);
        }
        var property = configData.GetType().GetProperty(configName);
        if (property is null)
        {
            return null;
        }
        return JsonSerializer.Serialize(property.GetValue(configData), SerializationOptions);
    }
}

public enum WeaponSelectionType
{
    PlayerChoice,
    Random,
    Default,
}

public enum DatabaseProvider
{
    Sqlite,
    MySql,
}

public enum RoundTypeSelectionOption
{
    Random,
    RandomFixedCounts,
    ManualOrdering,
}

public record RoundTypeManualOrderingItem(RoundType Type, int Count);

/// <summary>
/// The reduced utility pool for restricted teams (Ts defending the bomb, and the
/// force-buying team of a force-buy round). Molotov in these settings also means
/// Incendiary for CTs (and vice versa).
/// </summary>
public record RestrictedUtilityConfig
{
    public bool Enabled { get; set; } = true;

    public bool ApplyToTerrorists { get; set; } = true;
    public bool ApplyToForceBuyTeam { get; set; } = true;

    // Nade types a restricted team may receive at all.
    public List<CsItem> AllowedNades { get; set; } = new()
    {
        CsItem.Flashbang,
        CsItem.HE,
        CsItem.Smoke,
    };

    // Extra team-wide caps applied on top of MaxNades (the lower value wins).
    public Dictionary<CsItem, int> MaxTeamNadesOverride { get; set; } = new()
    {
        {CsItem.Smoke, 1},
    };
}

public record ConfigData
{
    public List<CsItem> UsableWeapons { get; set; } = WeaponHelpers.AllWeapons;

    public List<WeaponSelectionType> AllowedWeaponSelectionTypes { get; set; } =
        Enum.GetValues<WeaponSelectionType>().ToList();

    public Dictionary<CsTeam, Dictionary<WeaponAllocationType, CsItem>> DefaultWeapons { get; set; } =
        WeaponHelpers.DefaultWeaponsByTeamAndAllocationType;

    public Dictionary<
        string,
        Dictionary<
            CsTeam,
            Dictionary<CsItem, int>
        >
    > MaxNades { get; set; } = new()
    {
        {
            NadeHelpers.GlobalSettingName, new()
            {
                {
                    CsTeam.Terrorist, new()
                    {
                        {CsItem.Flashbang, 2},
                        {CsItem.Smoke, 1},
                        {CsItem.Molotov, 1},
                        {CsItem.HE, 1},
                    }
                },
                {
                    CsTeam.CounterTerrorist, new()
                    {
                        {CsItem.Flashbang, 2},
                        {CsItem.Smoke, 1},
                        {CsItem.Incendiary, 2},
                        {CsItem.HE, 1},
                    }
                },
            }
        }
    };

    public Dictionary<
        string,
        Dictionary<
            CsTeam,
            Dictionary<RoundType, MaxTeamNadesSetting>
        >
    > MaxTeamNades { get; set; } = new()
    {
        {
            NadeHelpers.GlobalSettingName, new()
            {
                {
                    CsTeam.Terrorist, new()
                    {
                        {RoundType.Pistol, MaxTeamNadesSetting.AverageOnePerPlayer},
                        {RoundType.HalfBuy, MaxTeamNadesSetting.AverageOnePointFivePerPlayer},
                        {RoundType.FullBuy, MaxTeamNadesSetting.AverageOnePointFivePerPlayer},
                    }
                },
                {
                    CsTeam.CounterTerrorist, new()
                    {
                        {RoundType.Pistol, MaxTeamNadesSetting.AverageOnePerPlayer},
                        {RoundType.HalfBuy, MaxTeamNadesSetting.AverageOnePointFivePerPlayer},
                        {RoundType.FullBuy, MaxTeamNadesSetting.AverageOnePointFivePerPlayer},
                    }
                },
            }
        }
    };

    // Relative weights of each nade type in the random utility distribution.
    // Molotov applies to Incendiary for CTs automatically.
    public Dictionary<CsItem, int> NadeDistributionWeights { get; set; } = new()
    {
        {CsItem.Flashbang, 4},
        {CsItem.Smoke, 3},
        {CsItem.HE, 3},
        {CsItem.Molotov, 2},
    };

    // How many nades of each type a single player may carry from the allocator.
    // Types missing from this list are never stacked on one player.
    public Dictionary<CsItem, int> MaxNadesPerPlayer { get; set; } = new()
    {
        {CsItem.Flashbang, 2},
        {CsItem.Smoke, 1},
        {CsItem.HE, 1},
        {CsItem.Molotov, 1},
        {CsItem.Incendiary, 1},
    };

    public RestrictedUtilityConfig RestrictedUtility { get; set; } = new();

    public RoundTypeSelectionOption RoundTypeSelection { get; set; } = RoundTypeSelectionOption.Random;

    public Dictionary<RoundType, int> RoundTypePercentages { get; set; } = new()
    {
        {RoundType.Pistol, 15},
        {RoundType.HalfBuy, 25},
        {RoundType.FullBuy, 60},
    };

    public Dictionary<RoundType, int> RoundTypeRandomFixedCounts { get; set; } = new()
    {
        {RoundType.Pistol, 5},
        {RoundType.HalfBuy, 10},
        {RoundType.FullBuy, 15},
    };

    public List<RoundTypeManualOrderingItem> RoundTypeManualOrdering { get; set; } = new()
    {
        new RoundTypeManualOrderingItem(RoundType.Pistol, 5),
        new RoundTypeManualOrderingItem(RoundType.HalfBuy, 10),
        new RoundTypeManualOrderingItem(RoundType.FullBuy, 15),
    };

    public bool MigrateOnStartup { get; set; } = true;
    public bool ResetStateOnGameRestart { get; set; } = true;
    public bool AllowAllocationAfterFreezeTime { get; set; } = true;

    // How long after the round goes live (freeze time end) weapon changes are still applied immediately.
    // After this window, selections are saved and given on the next applicable round.
    public double WeaponChangeWindowAfterRoundStartSeconds { get; set; } = 10;

    // On pistol rounds, players who take a non-default pistol give up their kevlar.
    public bool EnablePistolRoundEconomy { get; set; } = true;

    // Give each player exactly enough money for the weapons valid on their
    // EFFECTIVE round type, so everything above it renders greyed out in the
    // native buy menu (same look as not affording it). Money is topped back up
    // after every purchase so the visuals stay stable during the buy window.
    public bool AdjustMoneyToRoundType { get; set; } = true;

    public Dictionary<RoundType, int> MoneyByRoundType { get; set; } = new()
    {
        {RoundType.Pistol, 700},
        {RoundType.HalfBuy, 2500},
        {RoundType.FullBuy, 16000},
    };
    public bool UseOnTickFeatures { get; set; } = true;
    public bool CapabilityWeaponPaints { get; set; } = true;
    public bool EnableRoundTypeAnnouncement { get; set; } = true;
    public bool EnableRoundTypeAnnouncementCenter { get; set; } = false;
    public bool EnableBombSiteAnnouncementCenter { get; set; } = false;
    public bool BombSiteAnnouncementCenterToCTOnly { get; set; } = false;
    public bool DisableDefaultBombPlantedCenterMessage { get; set; } = false;
    public bool ForceCloseBombSiteAnnouncementCenterOnPlant { get; set; } = true;
    public float BombSiteAnnouncementCenterDelay { get; set; } = 1.0f;
    public float BombSiteAnnouncementCenterShowTimer { get; set; } = 5.0f;
    public bool EnableBombSiteAnnouncementChat { get; set; } = false;
    public bool EnableNextRoundTypeVoting { get; set; } = false;
    public int NumberOfExtraVipChancesForPreferredWeapon { get; set; } = 1;
    public bool AllowPreferredWeaponForEveryone { get; set; } = false;

    public double ChanceForPreferredWeapon { get; set; } = 100;

    public Dictionary<CsTeam, int> MaxPreferredWeaponsPerTeam { get; set; } = new()
    {
        {CsTeam.Terrorist, 1},
        {CsTeam.CounterTerrorist, 1},
    };

    public Dictionary<CsTeam, int> MinPlayersPerTeamForPreferredWeapon { get; set; } = new()
    {
        {CsTeam.Terrorist, 1},
        {CsTeam.CounterTerrorist, 1},
    };

    public bool EnableCanAcquireHook { get; set; } = true;

    public LogLevel LogLevel { get; set; } = LogLevel.Information;
    public string ChatMessagePluginName { get; set; } = "Retakes";
    public string? ChatMessagePluginPrefix { get; set; }

    public string InGameGunMenuCenterCommands { get; set; } =
        "gunsmenu,gunmenu,!gunmenu,!gunsmenu,!menugun,!menuguns,/gunsmenu,/gunmenu";

    public string InGameGunMenuChatCommands { get; set; } = "guns,!guns,/guns";
    public ZeusPreference ZeusPreference { get; set; } = ZeusPreference.Never;

    public DatabaseProvider DatabaseProvider { get; set; } = DatabaseProvider.Sqlite;
    public string DatabaseConnectionString { get; set; } = "Data Source=data.db; Pooling=False";
    public bool AutoUpdateSignatures { get; set; } = true;

    public IList<string> Validate()
    {
        if (RoundTypePercentages.Values.Sum() != 100)
        {
            throw new Exception("'RoundTypePercentages' values must add up to 100");
        }

        var warnings = new List<string>();
        warnings.AddRange(ValidateDefaultWeapons(CsTeam.Terrorist));
        warnings.AddRange(ValidateDefaultWeapons(CsTeam.CounterTerrorist));

        if (RestrictedUtility.Enabled && RestrictedUtility.AllowedNades.Count == 0)
        {
            warnings.Add(
                "RestrictedUtility is enabled with an empty AllowedNades list: " +
                "restricted teams will receive no utility at all.");
        }

        if (NadeDistributionWeights.Count == 0 || NadeDistributionWeights.Values.All(w => w <= 0))
        {
            warnings.Add("NadeDistributionWeights has no positive weights: nobody will receive utility.");
        }

        foreach (var warning in warnings)
        {
            Log.Warn($"[CONFIG WARNING] {warning}");
        }

        return warnings;
    }

    private ICollection<string> ValidateDefaultWeapons(CsTeam team)
    {
        var warnings = new List<string>();
        if (!DefaultWeapons.TryGetValue(team, out var defaultWeapons))
        {
            warnings.Add($"Missing {team} in DefaultWeapons config.");
            return warnings;
        }

        if (defaultWeapons.ContainsKey(WeaponAllocationType.Preferred))
        {
            throw new Exception(
                $"Preferred is not a valid default weapon allocation type " +
                $"for config DefaultWeapons.{team}.");
        }

        var allocationTypes = WeaponHelpers.WeaponAllocationTypes;
        allocationTypes.Remove(WeaponAllocationType.Preferred);
        // Zeus is a pseudo allocation type used to persist the per-player Zeus preference; it has no default weapon.
        allocationTypes.Remove(WeaponAllocationType.Zeus);

        foreach (var allocationType in allocationTypes)
        {
            if (!defaultWeapons.TryGetValue(allocationType, out var w))
            {
                warnings.Add($"Missing {allocationType} in DefaultWeapons.{team} config.");
                continue;
            }

            if (!WeaponHelpers.IsWeapon(w))
            {
                throw new Exception($"{w} is not a valid weapon in config DefaultWeapons.{team}.{allocationType}.");
            }

            if (!UsableWeapons.Contains(w))
            {
                warnings.Add(
                    $"{w} in the DefaultWeapons.{team}.{allocationType} config " +
                    $"is not in the UsableWeapons list.");
            }
        }

        return warnings;
    }

    public double GetRoundTypePercentage(RoundType roundType)
    {
        return Math.Round(RoundTypePercentages[roundType] / 100.0, 2);
    }

    public bool CanPlayersSelectWeapons()
    {
        return AllowedWeaponSelectionTypes.Contains(WeaponSelectionType.PlayerChoice);
    }

    public bool CanAssignRandomWeapons()
    {
        return AllowedWeaponSelectionTypes.Contains(WeaponSelectionType.Random);
    }

    public bool CanAssignDefaultWeapons()
    {
        return AllowedWeaponSelectionTypes.Contains(WeaponSelectionType.Default);
    }
}
