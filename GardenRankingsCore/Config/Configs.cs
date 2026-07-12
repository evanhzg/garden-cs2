using System.Text.Json;
using System.Text.Json.Serialization;
using GardenRankingsCore.Models;
using Microsoft.Extensions.Logging;

namespace GardenRankingsCore.Config;

public static class Configs
{
    public static class Shared
    {
        public static string? Module { get; set; }
    }

    private static readonly string ConfigDirectoryName = "config";
    // Garden merged plugin: the allocator core owns config/config.json in the same
    // plugin folder, so the rankings config gets its own file name.
    private static readonly string ConfigFileName = "rankings.json";

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

    public static ConfigData OverrideConfigDataForTests(ConfigData configData)
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

public enum DatabaseProvider
{
    Sqlite,
    MySql,
}

public record RatingWeightsConfig
{
    public double Kill { get; set; } = 0.30;
    public double Damage { get; set; } = 0.20;
    public double Survival { get; set; } = 0.15;
    public double Kast { get; set; } = 0.15;
    public double Impact { get; set; } = 0.20;

    [JsonIgnore] public double Total => Kill + Damage + Survival + Kast + Impact;
}

public record ImpactBonusesConfig
{
    public double OpeningKill { get; set; } = 0.40;
    public double OpeningDeathPenalty { get; set; } = -0.15;
    public double MultiKill2 { get; set; } = 0.10;
    public double MultiKill3 { get; set; } = 0.35;
    public double MultiKill4 { get; set; } = 0.70;
    public double MultiKill5 { get; set; } = 1.20;
    public double ClutchWinBase { get; set; } = 0.30;
    public double ClutchWinPerEnemy { get; set; } = 0.15;
    public double BombPlant { get; set; } = 0.25;
    public double BombDefuse { get; set; } = 0.40;
    public double TradeKill { get; set; } = 0.08;
    public double FlashAssist { get; set; } = 0.07;
    public double UtilityDamagePer100 { get; set; } = 0.15;
    public double TeamKillPenalty { get; set; } = -0.30;
}

public record RoundTypeRatingScale
{
    public double KillExpectationScale { get; set; } = 1.0;
    public double DamageExpectationScale { get; set; } = 1.0;
    public double RatingScale { get; set; } = 1.0;
}

public record RatingConfig
{
    public RatingWeightsConfig Weights { get; set; } = new();
    public ImpactBonusesConfig Impact { get; set; } = new();

    // Baselines that define what an "average" (1.00 rated) round looks like in a 5v5 retake.
    public double ExpectedKillsPerRound { get; set; } = 0.65;
    public double ExpectedDamagePerRound { get; set; } = 85;
    public double ExpectedSurvivalRate { get; set; } = 0.45;
    public double ExpectedKastRate { get; set; } = 0.70;
    public double ExpectedImpactPerRound { get; set; } = 0.35;

    public Dictionary<RetakesRoundType, RoundTypeRatingScale> RoundTypeScales { get; set; } = new()
    {
        {RetakesRoundType.Pistol, new RoundTypeRatingScale {KillExpectationScale = 0.90, DamageExpectationScale = 0.85}},
        {RetakesRoundType.ForceBuy, new RoundTypeRatingScale()},
        {RetakesRoundType.FullBuy, new RoundTypeRatingScale {KillExpectationScale = 1.05, DamageExpectationScale = 1.10}},
        {RetakesRoundType.Unknown, new RoundTypeRatingScale()},
    };

    public double RatingClampMin { get; set; } = 0.0;
    public double RatingClampMax { get; set; } = 5.0;

    public RoundTypeRatingScale GetRoundTypeScale(RetakesRoundType roundType)
    {
        return RoundTypeScales.TryGetValue(roundType, out var scale) ? scale : new RoundTypeRatingScale();
    }
}

public record EloConfig
{
    public int StartingElo { get; set; } = 5000;
    public int MinElo { get; set; } = 0;
    public int MaxElo { get; set; } = 35000;

    // Base ELO movement per round before modifiers.
    public double KFactor { get; set; } = 40;

    // Retakes is asymmetric: the defending Ts win most rounds. Expected win
    // probability baseline for the T side; CT gets 1 minus this.
    public double TBaseWinProbability { get; set; } = 0.62;

    // Additional T win probability offset per round type (eg. Ts are stronger on full buys).
    public Dictionary<RetakesRoundType, double> TWinProbabilityOffsetByRoundType { get; set; } = new()
    {
        {RetakesRoundType.Pistol, 0.0},
        {RetakesRoundType.ForceBuy, 0.0},
        {RetakesRoundType.FullBuy, 0.02},
        {RetakesRoundType.Unknown, 0.0},
    };

    // Win probability shift per extra player on your team (team size imbalance).
    public double PlayerCountAdvantageWinProbability { get; set; } = 0.08;

    // How much the ELO difference between the two teams shifts the expectation (0..1).
    public double EloInfluenceOnExpectation { get; set; } = 0.5;
    public double EloDivisor { get; set; } = 400;

    // Personal performance scaling: winners with a high round rating gain more,
    // losers with a high round rating lose less.
    public double PerformanceRatingReference { get; set; } = 1.0;
    public double PerformanceInfluence { get; set; } = 0.5;
    public double PerformanceMultiplierMin { get; set; } = 0.5;
    public double PerformanceMultiplierMax { get; set; } = 1.5;

    // Loss mitigations (0 = no effect, 1 = loss fully cancelled). The strongest
    // applicable mitigation wins; they do not stack.
    public double MitigationTeamKilled { get; set; } = 1.0;
    public double MitigationEarlyDeath { get; set; } = 0.5;
    public double MitigationEnemyGreatRound { get; set; } = 0.5;

    // "Killed early": died within this many seconds of the round going live
    // while having dealt at most this much damage.
    public double EarlyDeathSeconds { get; set; } = 10;
    public int EarlyDeathMaxDamage { get; set; } = 49;

    // "Someone in the opposing team had a great round".
    public double EnemyGreatRoundRatingThreshold { get; set; } = 2.5;
    public int EnemyGreatRoundKills { get; set; } = 4;

    // AFK players never gain or lose ELO.
    public bool AfkEloProtection { get; set; } = true;
}

public record RankedConfig
{
    // Minimum human players on T/CT for ranked mode.
    public int MinPlayers { get; set; } = 4;

    // Fully automatic mode: ranked turns ON as soon as MinPlayers are on teams
    // and OFF when the count drops below - no votes involved.
    public bool AutoActivate { get; set; } = true;

    // Share of eligible players that must accept the start vote (1.0 = everyone).
    public double VoteRequiredRatio { get; set; } = 1.0;
    public double VoteDurationSeconds { get; set; } = 30;
    public bool CountNonVotersAsAccept { get; set; } = false;

    // Exiting ranked: by default the initiator just confirms; optionally require a full vote.
    public bool StopRequiresVote { get; set; } = false;
    public double StopConfirmSeconds { get; set; } = 15;

    // Chat reminder that ranked is available once enough players are on.
    public bool AnnounceAvailability { get; set; } = true;
    public double AvailabilityAnnounceIntervalSeconds { get; set; } = 180;

    // In ranked mode, after a map change the warmup lasts until MinPlayers joined a team.
    public double RankedWarmupMaxSeconds { get; set; } = 3600;
}

public record AfkConfig
{
    // Consecutive fully-AFK rounds before the player is moved to spectators.
    public int RoundsBeforeSpectate { get; set; } = 2;

    // Minimum movement (units) during a round to not be considered AFK.
    public double MoveThresholdUnits { get; set; } = 10;
}

public record AnnouncementsConfig
{
    public bool JoinPlacement { get; set; } = true;
    public bool EloChangeToPlayer { get; set; } = true;
    public bool ServerRecordBroken { get; set; } = true;
    public bool PersonalBestBroken { get; set; } = true;
}

public record SeasonConfig
{
    // 0 disables automatic rotation; otherwise a new season starts after N days.
    public int AutoRotateDays { get; set; } = 0;
    public string SeasonNamePrefix { get; set; } = "Season";
}

public record ModeCvarsConfig
{
    // Applied on every map start and after every mode change, before the mode list.
    // Friendly fire is prioritized over the death replay (they conflict), hence
    // spec_replay_enable 0.
    public List<string> CommonCommands { get; set; } = new()
    {
        "mp_freezetime 2",
        "mp_friendlyfire 1",
        "spec_replay_enable 0",
        "mp_playercashawards 0",
        "mp_teamcashawards 0",
    };

    // Classic retakes: free spectating, teams shaken up every round.
    public List<string> ClassicCommands { get; set; } = new()
    {
        "mp_forcecamera 0",
    };

    // Ranked Retakes: locked-down spectating, no scramble.
    public List<string> RankedCommands { get; set; } = new()
    {
        "mp_forcecamera 1",
    };

    // Competitive Retakes: fixed teams, free spectating per spec.
    public List<string> CompetitiveCommands { get; set; } = new()
    {
        "mp_forcecamera 0",
    };

    // Shuffle T/CT humans every round while in classic mode (never in RR/CR).
    public bool ScrambleTeamsEachClassicRound { get; set; } = true;
}

public record CompetitiveConfig
{
    // 2v2 and 3v3 only.
    public List<int> AllowedTeamSizes { get; set; } = new() {2, 3};

    // MR12: two halves of 12, first to 13 wins, 12-12 is a draw.
    public int RoundsPerHalf { get; set; } = 12;

    // The losing team force-buys after this many consecutive lost rounds.
    public int ForceBuyAfterConsecutiveLosses { get; set; } = 3;

    public double VoteDurationSeconds { get; set; } = 30;
    public double StopConfirmSeconds { get; set; } = 15;

    // Team (duo/trio) ELO.
    public int TeamStartingElo { get; set; } = 5000;
    public double TeamEloKFactor { get; set; } = 60;

    // Cancel the match (no ELO) when a full roster has left at round end.
    public bool CancelWhenRosterEmpty { get; set; } = true;
}

public record ClutchConfig
{
    public bool Enabled { get; set; } = true;

    // Guarantee at least this many clutch rounds per map (classic mode only).
    public int MinPerMap { get; set; } = 2;

    // Random chance per eligible round.
    public int ChancePercent { get; set; } = 12;

    // If the guarantee is behind schedule by this many rounds, force one.
    public int ForceAfterRounds { get; set; } = 15;

    // No clutch rounds below this many team humans; at exactly this count, 1vX only.
    public int MinPlayers { get; set; } = 4;
}

public record ConfigData
{
    public string ChatMessagePluginName { get; set; } = "Rankings";
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    public DatabaseProvider DatabaseProvider { get; set; } = DatabaseProvider.Sqlite;
    public string DatabaseConnectionString { get; set; } = "Data Source=rankings.db; Pooling=False";

    public RankedConfig Ranked { get; set; } = new();
    public EloConfig Elo { get; set; } = new();
    public RatingConfig Rating { get; set; } = new();
    public AfkConfig Afk { get; set; } = new();
    public AnnouncementsConfig Announcements { get; set; } = new();
    public SeasonConfig Season { get; set; } = new();
    public ModeCvarsConfig ModeCvars { get; set; } = new();
    public CompetitiveConfig Competitive { get; set; } = new();
    public ClutchConfig Clutch { get; set; } = new();

    // Instant map-change chat commands (/d2, /mirage, ...). Values are map names
    // for changelevel, or "ws:<workshop id>" for host_workshop_map.
    public Dictionary<string, string> MapAliases { get; set; } = new()
    {
        {"d2", "de_dust2"}, {"dust2", "de_dust2"},
        {"mirage", "de_mirage"}, {"mir", "de_mirage"},
        {"inferno", "de_inferno"}, {"inf", "de_inferno"},
        {"nuke", "de_nuke"},
        {"overpass", "de_overpass"}, {"op", "de_overpass"},
        {"ancient", "de_ancient"},
        {"anubis", "de_anubis"},
        {"vertigo", "de_vertigo"},
        {"train", "de_train"},
        // Cache is a workshop map: replace the id with the port you use, eg.
        // {"cache", "ws:123456789"},
    };

    // Map commands are open to everyone, but blocked while RR/CR is running
    // (admins always bypass).
    public bool BlockMapChangeDuringMatch { get; set; } = true;

    // Stats collection tuning
    public double TradeWindowSeconds { get; set; } = 3.0;
    public double MinBlindDurationSeconds { get; set; } = 0.7;

    // Premier-style scoreboard rank display
    public bool EnableScoreboardRanks { get; set; } = true;

    public IList<string> Validate()
    {
        var warnings = new List<string>();

        if (Ranked.MinPlayers < 2)
        {
            warnings.Add("Ranked.MinPlayers below 2 makes little sense; consider raising it.");
        }

        if (Ranked.VoteRequiredRatio is < 0 or > 1)
        {
            throw new Exception("Ranked.VoteRequiredRatio must be between 0 and 1.");
        }

        if (Elo.TBaseWinProbability is <= 0 or >= 1)
        {
            throw new Exception("Elo.TBaseWinProbability must be strictly between 0 and 1.");
        }

        if (Rating.Weights.Total <= 0)
        {
            throw new Exception("Rating.Weights must sum to a positive value.");
        }

        if (Elo.MinElo > Elo.StartingElo || Elo.StartingElo > Elo.MaxElo)
        {
            throw new Exception("Elo bounds must satisfy MinElo <= StartingElo <= MaxElo.");
        }

        foreach (var warning in warnings)
        {
            Log.Warn($"[CONFIG WARNING] {warning}");
        }

        return warnings;
    }
}
