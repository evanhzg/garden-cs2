using System.Text.Json.Serialization;

namespace RetakesPlugin.Garden;

/// <summary>
/// Config section for everything Garden adds on top of the base retakes plugin.
/// One sub-section per module; every module has an enable flag (ROADMAP R0).
/// </summary>
public class GardenSettings
{
    [JsonPropertyName("Admin")]
    public GardenAdminSettings Admin { get; set; } = new();

    /// <summary>
    /// Weapon/utility allocator (port of Garden-allocator). Its own detailed
    /// config stays in config.json inside the plugin folder — copy the one
    /// from your Garden-allocator install to keep every setting and preference DB.
    /// </summary>
    [JsonPropertyName("Allocator")]
    public ModuleToggleSettings Allocator { get; set; } = new() { Enabled = true };

    /// <summary>
    /// Rankings/ELO/CR/clutch (port of Garden-rankings). Its own detailed config
    /// is config/rankings.json in the plugin folder — copy your Garden-rankings
    /// config/config.json there (renamed) to keep every setting.
    /// </summary>
    [JsonPropertyName("Rankings")]
    public ModuleToggleSettings Rankings { get; set; } = new() { Enabled = true };

    [JsonPropertyName("InstantDefuse")]
    public InstantDefuseSettings InstantDefuse { get; set; } = new();

    [JsonPropertyName("SmallServer")]
    public SmallServerSettings SmallServer { get; set; } = new();

    [JsonPropertyName("Duels")]
    public DuelsSettings Duels { get; set; } = new();

    [JsonPropertyName("Executes")]
    public ExecutesSettings Executes { get; set; } = new();

    [JsonPropertyName("FastStrat")]
    public ModuleToggleSettings FastStrat { get; set; } = new();

    [JsonPropertyName("Spotlight")]
    public SpotlightSettings Spotlight { get; set; } = new();

    [JsonPropertyName("ServerControl")]
    public ServerControlSettings ServerControl { get; set; } = new();
}

/// <summary>
/// Live server-tuning toggles exposed through the <c>!gmenu</c> config menu.
/// These cvar-backed values are the source of truth and are re-applied on every
/// map start (so a setup stays consistent across map changes — a plain cvar set
/// in console would reset). Half-buy / ranked / scramble toggles live in the
/// allocator + rankings configs and are edited straight there by the menu.
/// </summary>
public class ServerControlSettings
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>mp_friendlyfire — team damage (TK) on/off.</summary>
    [JsonPropertyName("FriendlyFire")]
    public bool FriendlyFire { get; set; } = true;

    /// <summary>mp_forcecamera — true locks dead players to team-only spectating.</summary>
    [JsonPropertyName("ForceCamera")]
    public bool ForceCamera { get; set; } = false;

    /// <summary>mp_freezetime (seconds).</summary>
    [JsonPropertyName("FreezeTime")]
    public int FreezeTime { get; set; } = 2;

    /// <summary>mp_roundtime_defuse (minutes).</summary>
    [JsonPropertyName("RoundTimeMinutes")]
    public double RoundTimeMinutes { get; set; } = 1.92;

    /// <summary>mp_buy_anywhere — buy from anywhere on the map.</summary>
    [JsonPropertyName("BuyAnywhere")]
    public bool BuyAnywhere { get; set; } = false;

    /// <summary>sv_infinite_ammo — 0 off, 2 infinite reserve (fun toggle).</summary>
    [JsonPropertyName("InfiniteAmmo")]
    public bool InfiniteAmmo { get; set; } = false;
}

/// <summary>
/// "Spotlight" — a fun module that keeps an eye on specific player(s). Its
/// headline use: warn the CTs when a known rusher (default: Damien) pushes a
/// defined map zone in the first seconds of the round, plus a few gag effects
/// (see-through-walls glow, no-jump for a round). Everything is opt-in and
/// toggleable live; alerts fire only inside the early-round window.
/// </summary>
public class SpotlightSettings
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>SteamID64s the module watches. Default: Damien (vz7y).</summary>
    [JsonPropertyName("Targets")]
    public List<ulong> Targets { get; set; } = [76561198168713796];

    /// <summary>Friendly name shown in alerts (falls back to the in-game name).</summary>
    [JsonPropertyName("Alias")]
    public string Alias { get; set; } = "Damien";

    /// <summary>Only warn about a push in the first N seconds after freeze end.</summary>
    [JsonPropertyName("AlertWindowSeconds")]
    public double AlertWindowSeconds { get; set; } = 15;

    /// <summary>Who hears the push alert: "CT" (default), "T", or "all".</summary>
    [JsonPropertyName("AlertAudience")]
    public string AlertAudience { get; set; } = "CT";

    /// <summary>Only alert while the target is on T (the rushing side). false = any team.</summary>
    [JsonPropertyName("AlertOnlyWhenT")]
    public bool AlertOnlyWhenT { get; set; } = true;

    /// <summary>Default duration (seconds) of !reveal when no time is given.</summary>
    [JsonPropertyName("RevealDefaultSeconds")]
    public double RevealDefaultSeconds { get; set; } = 30;

    /// <summary>Auto: glow the target (visible through walls to everyone) every round. Off by default.</summary>
    [JsonPropertyName("AutoReveal")]
    public bool AutoReveal { get; set; } = false;

    /// <summary>Auto: the target can't jump, every round (gag). Off by default.</summary>
    [JsonPropertyName("AutoNoJump")]
    public bool AutoNoJump { get; set; } = false;
}

public class GardenAdminSettings
{
    /// <summary>SteamID64s that are always Owner-level (bootstrap; not removable in game).</summary>
    [JsonPropertyName("OwnerSteamIds")]
    public List<ulong> OwnerSteamIds { get; set; } = [];

    /// <summary>
    /// R10: also register the short command names (!admin, !kick, !slay, !map,
    /// !rcon). Enable once the legacy standalone plugins are retired — the g-
    /// prefixes only existed to avoid collisions during the transition.
    /// </summary>
    [JsonPropertyName("EnableShortAliases")]
    public bool EnableShortAliases { get; set; } = false;
}

public class InstantDefuseSettings
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Don't insta-defuse while an HE/molotov is in the air or a fire is burning.</summary>
    [JsonPropertyName("BlockOnUtilityDanger")]
    public bool BlockOnUtilityDanger { get; set; } = true;
}

public class SmallServerSettings
{
    /// <summary>"Auto" | "On" | "Off" — Auto activates at 1..MaxHumans humans.</summary>
    [JsonPropertyName("Mode")]
    public string Mode { get; set; } = "Auto";

    [JsonPropertyName("MaxHumans")]
    public int MaxHumans { get; set; } = 3;

    /// <summary>Total nades per team per round while the overlay is active (R5).</summary>
    [JsonPropertyName("MaxTeamNades")]
    public int MaxTeamNades { get; set; } = 2;

    /// <summary>Prefer spawns flagged "smallserver" (place them with !gspawn) while active.</summary>
    [JsonPropertyName("UseFlaggedSpawns")]
    public bool UseFlaggedSpawns { get; set; } = true;

    /// <summary>End the round instantly (T win) when the last CT dies while active.</summary>
    [JsonPropertyName("InstantRoundSwitchOnLastCtDeath")]
    public bool InstantRoundSwitchOnLastCtDeath { get; set; } = true;
}

public class ModuleToggleSettings
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = false;
}

public class ExecutesSettings
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("TWeapons")]
    public List<string> TWeapons { get; set; } = ["weapon_ak47", "weapon_deagle"];

    [JsonPropertyName("CtWeapons")]
    public List<string> CtWeapons { get; set; } = ["weapon_m4a1", "weapon_deagle"];

    [JsonPropertyName("GiveKevlarHelmet")]
    public bool GiveKevlarHelmet { get; set; } = true;

    /// <summary>Console commands executed when Executes starts.</summary>
    [JsonPropertyName("StartCommands")]
    public List<string> StartCommands { get; set; } =
    [
        "mp_warmup_end",
        "mp_freezetime 3",
        "mp_roundtime_defuse 1.92",
        "mp_restartgame 1",
    ];

    /// <summary>Console commands executed when Executes stops (back to retakes).</summary>
    [JsonPropertyName("StopCommands")]
    public List<string> StopCommands { get; set; } = ["mp_restartgame 1"];
}

public class DuelsSettings
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Max distance (units) between two "duel"-flagged spawns to form an arena.</summary>
    [JsonPropertyName("MaxPairDistance")]
    public double MaxPairDistance { get; set; } = 1500;

    /// <summary>Max lanes running at once (R8 parallel duels; also capped by arena count).</summary>
    [JsonPropertyName("MaxParallelDuels")]
    public int MaxParallelDuels { get; set; } = 3;

    /// <summary>R10: dead/queued players auto-spectate the duel that just started.</summary>
    [JsonPropertyName("SpectatorAutoFollow")]
    public bool SpectatorAutoFollow { get; set; } = true;

    /// <summary>Weapons every duelist gets on (re)spawn (knife is always kept).</summary>
    [JsonPropertyName("Weapons")]
    public List<string> Weapons { get; set; } = ["weapon_ak47", "weapon_deagle"];

    [JsonPropertyName("GiveKevlarHelmet")]
    public bool GiveKevlarHelmet { get; set; } = true;

    /// <summary>Console commands executed when Duels starts (round flow is manual).</summary>
    [JsonPropertyName("StartCommands")]
    public List<string> StartCommands { get; set; } =
    [
        "mp_warmup_end",
        "mp_ignore_round_win_conditions 1",
        "mp_roundtime 60",
        "mp_freezetime 1",
        "mp_maxmoney 0",
        "mp_restartgame 1",
    ];

    /// <summary>Console commands executed when Duels stops (back to retakes).</summary>
    [JsonPropertyName("StopCommands")]
    public List<string> StopCommands { get; set; } =
    [
        "mp_ignore_round_win_conditions 0",
        "mp_restartgame 1",
    ];
}
