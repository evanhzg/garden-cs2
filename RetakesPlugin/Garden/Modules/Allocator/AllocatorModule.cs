using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore.Managers;
using RetakesAllocator.Menus;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;
using SQLitePCL;
using RetakesAllocator.AdvancedMenus;
using static RetakesAllocatorCore.PluginInfo;
using RetakesPluginShared.Events;
using RetakesAllocatorShared;
using RetakesPlugin.Garden;
using GardenPlugin = RetakesPlugin.RetakesPlugin;

namespace RetakesAllocator;

/// <summary>
/// Garden port of yonilerner/cs2-retakes-allocator (Garden-allocator fork) as a
/// module of the merged plugin (ROADMAP R0). The donor plugin's logic is kept
/// intact; only the BasePlugin surface changed:
///  - [GameEventHandler]/[ConsoleCommand] attributes -> explicit registrations in Load
///  - Localizer/AddTimer/RegisterListener/ModuleDirectory -> _plugin.*
///  - retakes event sender capability lookup -> direct _plugin.EventSender subscription
///  - map-start listener -> IGardenModule.OnMapStart
/// The IRetakesAllocatorApi capability is still registered so Garden-rankings
/// (separate plugin, for now) keeps reading round types.
/// </summary>
public class AllocatorModule : IGardenModule
{
    public static AllocatorModule? Instance { get; private set; }

    private readonly GardenPlugin _plugin;
    private readonly GardenHost _host;

    private readonly AllocatorMenuManager _allocatorMenuManager = new();
    private readonly AdvancedGunMenu _advancedGunMenu = new();
    private readonly Dictionary<CCSPlayerController, Dictionary<ItemSlotType, CsItem>> _allocatedPlayerItems = new();

    private CustomGameData? CustomFunctions { get; set; }

    private bool IsAllocatingForRound { get; set; }
    private string _bombsite = "";
    private bool _announceBombsite;
    private bool _bombsiteAnnounceOneTime;

    public string Name => "Allocator";
    public bool Enabled => _host.Settings.Allocator.Enabled;

    public AllocatorModule(GardenPlugin plugin, GardenHost host)
    {
        _plugin = plugin;
        _host = host;
    }

    #region Setup

    /// <summary>
    /// Exposes round type information to other plugins (eg. GardenRankings).
    /// </summary>
    private class AllocatorApi : IRetakesAllocatorApi
    {
        public int? CurrentRoundTypeOrdinal =>
            RoundTypeManager.Instance.GetCurrentRoundType() is { } rt ? (int) rt : null;

        public string? CurrentRoundTypeName =>
            RoundTypeManager.Instance.GetCurrentRoundType()?.ToString();

        public int? ForceBuyTeamNum =>
            RoundTypeManager.Instance.ForceBuyTeam is { } team ? (int) team : null;

        public int? GetEffectiveRoundTypeOrdinal(int teamNum) =>
            RoundTypeManager.Instance.GetCurrentEffectiveRoundType((CsTeam) teamNum) is { } rt ? (int) rt : null;

        public void SetNextRoundTypeOverride(int? roundTypeOrdinal) =>
            RoundTypeManager.Instance.SetNextRoundTypeOverride(
                roundTypeOrdinal is null ? null : (RoundType) roundTypeOrdinal.Value);

        public void SetNextForceBuyTeam(int? teamNum) =>
            RoundTypeManager.Instance.SetNextForceBuyTeamOverride(
                teamNum is null ? null : (CsTeam) teamNum.Value);
    }

    private static readonly PluginCapability<IRetakesAllocatorApi> AllocatorApiCapability =
        new(RetakesAllocatorApiCapability.Name);

    public void Load(bool hotReload)
    {
        if (!Enabled)
        {
            Log.Info("Allocator module disabled via GardenSettings.");
            return;
        }

        Instance = this;
        Configs.Shared.Module = _plugin.ModuleDirectory;

        Capabilities.RegisterPluginCapability(AllocatorApiCapability, () => new AllocatorApi());

        Log.Debug($"Allocator module loaded. Hot reload: {hotReload}");
        ResetState();
        Batteries.Init();

        if (_plugin.Config.Game.EnableFallbackAllocation)
        {
            Log.Warn("GameSettings.EnableFallbackAllocation is true while the Garden allocator module " +
                     "is enabled — set it to false to avoid double allocation.");
        }

        _ = Task.Run(async () =>
        {
            var downloadedNewGameData = await Helpers.DownloadMissingFiles();
            if (!downloadedNewGameData)
            {
                return;
            }

            Server.NextFrame(() =>
            {
                CustomFunctions ??= new();
                // Must unhook the old functions before reloading and rehooking
                CustomFunctions.CCSPlayer_ItemServices_CanAcquireFunc?.Unhook(OnWeaponCanAcquire, HookMode.Pre);
                CustomFunctions.LoadCustomGameData();
                if (Configs.GetConfigData().EnableCanAcquireHook)
                {
                    CustomFunctions.CCSPlayer_ItemServices_CanAcquireFunc?.Hook(OnWeaponCanAcquire, HookMode.Pre);
                }
            });
        });

        if (Configs.GetConfigData().UseOnTickFeatures)
        {
            _plugin.RegisterListener<Listeners.OnTick>(OnTick);
        }

        // Same-assembly now: subscribe to the internal retakes event sender directly.
        _plugin.EventSender.RetakesPluginEventHandlers += RetakesEventHandler;

        if (Configs.GetConfigData().MigrateOnStartup)
        {
            Queries.Migrate();
        }

        CustomFunctions = new();

        if (Configs.GetConfigData().EnableCanAcquireHook)
        {
            CustomFunctions.CCSPlayer_ItemServices_CanAcquireFunc?.Hook(OnWeaponCanAcquire, HookMode.Pre);
        }

        // Event handlers (donor used [GameEventHandler] attributes).
        _plugin.RegisterEventHandler<EventItemPurchase>(OnPostItemPurchase);
        _plugin.RegisterEventHandler<EventBombPlanted>(OnEventBombPlanted, HookMode.Pre);
        _plugin.RegisterEventHandler<EventRoundStart>(OnEventRoundStart);
        _plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnEventRoundFreezeEnd);
        _plugin.RegisterEventHandler<EventRoundEnd>(OnEventRoundEnd);
        _plugin.RegisterEventHandler<EventEnterBombzone>(OnEventEnterBombzone);
        _plugin.RegisterEventHandler<EventPlayerDisconnect>(OnEventPlayerDisconnect);
        // CSS removed EventPlayerChat after 1.0.329: chat menu triggers now come
        // from "say"/"say_team" command listeners instead.
        _plugin.AddCommandListener("say", OnSayCommand, HookMode.Post);
        _plugin.AddCommandListener("say_team", OnSayCommand, HookMode.Post);
        _plugin.RegisterEventHandler<EventRoundAnnounceWarmup>(OnEventRoundAnnounceWarmup);

        // Commands (donor used [ConsoleCommand] attributes).
        _plugin.AddCommand("css_nextround", "Opens the menu to vote for the next round type.", OnNextRoundCommand);
        _plugin.AddCommand("css_gun", "Set a weapon preference. Usage: !gun <gun> [T|CT]", OnWeaponCommand);
        _plugin.AddCommand("css_ak", "Make the AK-47 your full-buy primary on both teams.", OnAkCommand);
        _plugin.AddCommand("css_ak47", "Make the AK-47 your full-buy primary on both teams.", OnAkCommand);
        _plugin.AddCommand("css_m4a4", "Make the M4A4 your full-buy primary on both teams.", OnM4A4Command);
        _plugin.AddCommand("css_m4a1", "Make the M4A1-S your full-buy primary on both teams.", OnM4A1SCommand);
        _plugin.AddCommand("css_m4a1s", "Make the M4A1-S your full-buy primary on both teams.", OnM4A1SCommand);
        _plugin.AddCommand("css_awp", "Join or leave the AWP queue.", OnAwpCommand);
        _plugin.AddCommand("css_zeus", "Toggle whether you get a free Zeus every round.", OnZeusCommand);
        _plugin.AddCommand("css_removegun", "Remove a weapon preference. Usage: !removegun <gun> [T|CT]", OnRemoveWeaponCommand);
        _plugin.AddCommand("css_setnextround", "Sets the next round type. Usage: !setnextround <P/H/F>", OnSetNextRoundCommand);
        _plugin.AddCommand("css_reload_allocator_config", "Reloads the allocator config.", OnReloadAllocatorConfigCommand);
        _plugin.AddCommand("css_print_config", "Print the entire allocator config or a specific one.", OnPrintConfigCommand);
    }

    /// <summary>
    /// R12 (preference persistence): a relative SQLite path resolves against the
    /// server's CWD — not the plugin folder — and gets wiped by redeploys, which
    /// is why weapon prefs vanished. Anchor it to the plugin dir (in-memory only).
    /// For rock-solid persistence set DatabaseProvider "MySql" +
    /// DatabaseConnectionString in config/config.json (shared Garden DB works).
    /// </summary>
    private void FixupDatabasePath()
    {
        var cfg = Configs.GetConfigData();
        if (cfg.DatabaseProvider == DatabaseProvider.Sqlite &&
            cfg.DatabaseConnectionString.Contains("data.db") &&
            !cfg.DatabaseConnectionString.Contains('/') &&
            !cfg.DatabaseConnectionString.Contains('\\'))
        {
            cfg.DatabaseConnectionString = cfg.DatabaseConnectionString.Replace(
                "data.db", Path.Combine(_plugin.ModuleDirectory, "data.db"));
            Log.Info($"SQLite path anchored to plugin dir: {cfg.DatabaseConnectionString}");
        }
    }

    public void OnMapStart(string mapName)
    {
        if (!Enabled)
        {
            return;
        }

        ResetState();
        Log.Debug($"Setting map name {mapName}");
        RoundTypeManager.Instance.SetMap(mapName);
    }

    private void ResetState(bool loadConfig = true)
    {
        if (loadConfig)
        {
            Configs.Load(_plugin.ModuleDirectory, true);
            FixupDatabasePath();
        }

        Translator.Initialize(_plugin.Localizer);

        RoundTypeManager.Instance.SetNextRoundTypeOverride(null);
        RoundTypeManager.Instance.SetCurrentRoundType(null);
        RoundTypeManager.Instance.Initialize();

        _allocatedPlayerItems.Clear();
        _bombsite = "";
        _announceBombsite = false;
        _bombsiteAnnounceOneTime = false;
    }

    public void Unload()
    {
        if (!Enabled)
        {
            return;
        }

        Log.Debug("Allocator module unloaded");
        Instance = null;
        ResetState(loadConfig: false);
        Queries.Disconnect();

        _plugin.EventSender.RetakesPluginEventHandlers -= RetakesEventHandler;

        if (Configs.GetConfigData().EnableCanAcquireHook && CustomFunctions != null)
        {
            CustomFunctions.CCSPlayer_ItemServices_CanAcquireFunc?.Unhook(OnWeaponCanAcquire, HookMode.Pre);
        }
    }

    private void RetakesEventHandler(object? _, IRetakesPluginEvent @event)
    {
        Log.Trace("Got retakes event");
        Action? handler = @event switch
        {
            AllocateEvent => HandleAllocateEvent,
            _ => null
        };
        handler?.Invoke();
    }

    /// <summary>Replacement for the [RequiresPermissions("@css/root")] attribute (console always passes).</summary>
    private static bool HasRootPermission(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null || AdminManager.PlayerHasPermissions(player, "@css/root"))
        {
            return true;
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}You don't have permission to use this command.");
        return false;
    }

    #endregion

    #region Commands

    public void OnNextRoundCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}This command can only be executed by a valid player.");
            return;
        }

        if (!Configs.GetConfigData().EnableNextRoundTypeVoting)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Next round voting is disabled.");
            return;
        }

        _allocatorMenuManager.OpenMenuForPlayer(player!, MenuType.NextRoundVote);
    }

    public void OnWeaponCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        HandleWeaponCommand(player, commandInfo);
    }

    private void HandleWeaponCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        var currentTeam = player!.Team;

        var result = OnWeaponCommandHelper.Handle(
            Helpers.CommandInfoToArgList(commandInfo),
            playerId,
            RoundTypeManager.Instance.GetCurrentEffectiveRoundType(currentTeam),
            currentTeam,
            false,
            out var selectedWeapon
        );
        Helpers.WriteNewlineDelimited(result, commandInfo.ReplyToCommand);

        if (selectedWeapon is not null)
        {
            ApplyImmediateWeaponChange(player!, selectedWeapon.Value);
        }
    }

    /// <summary>
    /// Applies a weapon selection to the player's current loadout if the weapon change window
    /// (freeze time + a few seconds after round start) is still open. Otherwise the already-saved
    /// preference will simply be used on the next round.
    /// Also enforces the pistol-round economy rule: switching off the default pistol strips kevlar.
    /// </summary>
    public void ApplyImmediateWeaponChange(CCSPlayerController player, CsItem weapon)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var team = player.Team;
        var effectiveRoundType = RoundTypeManager.Instance.GetCurrentEffectiveRoundType(team);
        if (effectiveRoundType is null)
        {
            return;
        }

        if (!Helpers.IsWeaponAllocationAllowed())
        {
            Helpers.WriteNewlineDelimited(
                Translator.Instance["weapon_preference.buy_window_closed"],
                player.PrintToChat
            );
            return;
        }

        var selectedWeaponAllocationType =
            WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(effectiveRoundType, team, weapon);
        if (
            selectedWeaponAllocationType is null ||
            !WeaponHelpers.IsAllocationTypeValidForRound(selectedWeaponAllocationType, effectiveRoundType)
        )
        {
            return;
        }

        Helpers.RemoveWeapons(
            player,
            item =>
                WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(effectiveRoundType, team, item) ==
                selectedWeaponAllocationType
        );

        var slotType = WeaponHelpers.GetSlotTypeForItem(weapon);
        var slot = WeaponHelpers.GetSlotNameForSlotType(slotType);
        AllocateItemsForPlayer(player, new List<CsItem> {weapon}, slot);

        ApplyPistolRoundEconomy(player, effectiveRoundType.Value, selectedWeaponAllocationType.Value, weapon);
    }

    /// <summary>
    /// On a pistol round, a player who swaps to a non-default pistol loses their kevlar,
    /// even when the swap happens after round start. Swapping back to the default pistol
    /// (Glock / USP-S / P2000) gives the kevlar back.
    /// </summary>
    private void ApplyPistolRoundEconomy(CCSPlayerController player, RoundType effectiveRoundType,
        WeaponAllocationType allocationType, CsItem weapon)
    {
        if (
            !Configs.GetConfigData().EnablePistolRoundEconomy ||
            effectiveRoundType != RoundType.Pistol ||
            allocationType != WeaponAllocationType.PistolRound
        )
        {
            return;
        }

        if (WeaponHelpers.GetDefaultPistol(player.Team) is null)
        {
            return;
        }

        if (WeaponHelpers.IsDefaultPistol(player.Team, weapon))
        {
            // Back on the default pistol: restore kevlar if it was given up.
            if (Helpers.GiveKevlarIfMissing(player))
            {
                Helpers.WriteNewlineDelimited(
                    Translator.Instance["weapon_preference.economy_kevlar_back", weapon],
                    player.PrintToChat
                );
            }

            return;
        }

        Helpers.StripArmor(player);
        Helpers.WriteNewlineDelimited(
            Translator.Instance["weapon_preference.economy_no_kevlar", weapon],
            player.PrintToChat
        );
    }

    public void OnAkCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        HandleCrossTeamPrimaryCommand(player, commandInfo, CsItem.AK47);
    }

    public void OnM4A4Command(CCSPlayerController? player, CommandInfo commandInfo)
    {
        HandleCrossTeamPrimaryCommand(player, commandInfo, CsItem.M4A4);
    }

    public void OnM4A1SCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        HandleCrossTeamPrimaryCommand(player, commandInfo, CsItem.M4A1S);
    }

    /// <summary>
    /// Sets a full-buy primary preference for BOTH teams (bypassing the normal
    /// team-validity rules, eg. AK on CT) and applies it immediately when the
    /// ongoing round is a full buy and the buy window is still open.
    /// </summary>
    private void HandleCrossTeamPrimaryCommand(CCSPlayerController? player, CommandInfo commandInfo, CsItem weapon)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        if (playerId == 0)
        {
            commandInfo.ReplyToCommand(
                $"{MessagePrefix}{Translator.Instance["weapon_preference.not_saved"]}");
            return;
        }

        if (!Configs.GetConfigData().CanPlayersSelectWeapons())
        {
            commandInfo.ReplyToCommand(
                $"{MessagePrefix}{Translator.Instance["weapon_preference.cannot_choose"]}");
            return;
        }

        Queries.SetWeaponPreferenceForUser(playerId, CsTeam.Terrorist,
            WeaponAllocationType.FullBuyPrimary, weapon);
        Queries.SetWeaponPreferenceForUser(playerId, CsTeam.CounterTerrorist,
            WeaponAllocationType.FullBuyPrimary, weapon);

        commandInfo.ReplyToCommand(
            $"{MessagePrefix}{Translator.Instance["weapon_preference.cross_team_primary", weapon]}");

        // Swap right away when possible.
        var team = player!.Team;
        if (team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist) || !player.PawnIsAlive)
        {
            return;
        }

        var effectiveRoundType = RoundTypeManager.Instance.GetCurrentEffectiveRoundType(team);
        if (effectiveRoundType != RoundType.FullBuy)
        {
            Helpers.WriteNewlineDelimited(
                Translator.Instance["weapon_preference.receive_next_round",
                    RoundTypeHelpers.TranslateRoundTypeName(RoundType.FullBuy)],
                player.PrintToChat);
            return;
        }

        if (!Helpers.IsWeaponAllocationAllowed())
        {
            Helpers.WriteNewlineDelimited(
                Translator.Instance["weapon_preference.buy_window_closed"],
                player.PrintToChat);
            return;
        }

        Helpers.RemoveWeapons(player,
            item => WeaponHelpers.GetSlotTypeForItem(item) == ItemSlotType.Primary);
        AllocateItemsForPlayer(player, new List<CsItem> {weapon},
            WeaponHelpers.GetSlotNameForSlotType(ItemSlotType.Primary));
    }

    public void OnAwpCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        if (playerId == 0)
        {
            commandInfo.ReplyToCommand("Cannot save preferences with invalid Steam ID.");
            return;
        }

        var currentTeam = player!.Team;

        if (Configs.GetConfigData().NumberOfExtraVipChancesForPreferredWeapon == -1 && !Helpers.IsVip(player))
        {
            var message = Translator.Instance["weapon_preference.only_vip_can_use"];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
            return;
        }

        var result = Task.Run(async () =>
        {
            var currentPreferredSetting = (await Queries.GetUserSettings(playerId))
                ?.GetWeaponPreference(currentTeam, WeaponAllocationType.Preferred);

            return await OnWeaponCommandHelper.HandleAsync(
                new List<string> {CsItem.AWP.ToString()},
                playerId,
                RoundTypeManager.Instance.GetCurrentEffectiveRoundType(currentTeam),
                currentTeam,
                currentPreferredSetting is not null
            );
        }).Result;
        Helpers.WriteNewlineDelimited(result.Item1, commandInfo.ReplyToCommand);
    }

    public void OnZeusCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        if (playerId == 0)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}{Translator.Instance["weapon_preference.not_saved"]}");
            return;
        }

        var enabled = Task.Run(async () =>
        {
            var userSetting = await Queries.GetUserSettings(playerId);
            var newValue = !(userSetting?.GetZeusPreference() ?? false);
            await Queries.SetZeusPreferenceAsync(playerId, newValue);
            return newValue;
        }).Result;

        ApplyZeusChangeNow(player!, enabled);

        var message = Translator.Instance[enabled ? "zeus.enabled" : "zeus.disabled"];
        commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
    }

    /// <summary>
    /// Gives or removes the Zeus immediately when a player toggles the preference mid-round.
    /// It's free, so no buy window restriction applies. The give is synchronous (no timer)
    /// so a quick enable/disable toggle can never remove the taser on the frame it spawns.
    /// </summary>
    public void ApplyZeusChangeNow(CCSPlayerController player, bool enabled)
    {
        if (!Helpers.PlayerIsValid(player) || !player.PawnIsAlive)
        {
            return;
        }

        if (enabled)
        {
            var existing = Helpers.GetPlayerWeaponItem(player, i => i == CsItem.Taser);
            if (existing is null)
            {
                player.GiveNamedItem("weapon_taser");
            }
        }
        else
        {
            Helpers.RemoveWeapons(player, i => i == CsItem.Taser);
        }
    }

    public void OnRemoveWeaponCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        if (commandInfo.ArgCount < 2)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Usage: !removegun <gun> [T|CT]");
            return;
        }

        var playerId = Helpers.GetSteamId(player);
        var currentTeam = player!.Team;

        var result = OnWeaponCommandHelper.Handle(
            Helpers.CommandInfoToArgList(commandInfo),
            playerId,
            RoundTypeManager.Instance.GetCurrentRoundType(),
            currentTeam,
            true,
            out _
        );
        commandInfo.ReplyToCommand($"{MessagePrefix}{result}");
    }

    public void OnSetNextRoundCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        if (commandInfo.ArgCount < 2)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Usage: !setnextround <P/H/F>");
            return;
        }

        var roundTypeInput = commandInfo.GetArg(1).ToLower();
        var roundType = RoundTypeHelpers.ParseRoundType(roundTypeInput);
        if (roundType is null)
        {
            var message = Translator.Instance["announcement.next_roundtype_set_invalid", roundTypeInput];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
        }
        else
        {
            RoundTypeManager.Instance.SetNextRoundTypeOverride(roundType);
            var roundTypeName = RoundTypeHelpers.TranslateRoundTypeName(roundType.Value);
            var message = Translator.Instance["announcement.next_roundtype_set", roundTypeName];
            commandInfo.ReplyToCommand($"{MessagePrefix}{message}");
        }
    }

    public void OnReloadAllocatorConfigCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}Reloading config for version {PluginInfo.Version}");
        Configs.Load(_plugin.ModuleDirectory);
        FixupDatabasePath();
        RoundTypeManager.Instance.Initialize();
    }

    public void OnPrintConfigCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!HasRootPermission(player, commandInfo))
        {
            return;
        }

        var configName = commandInfo.ArgCount > 1 ? commandInfo.GetArg(1) : null;
        var response = Configs.StringifyConfig(configName);
        if (response is null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Invalid config name.");
            return;
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}{response}");
        Log.Info(response);
    }

    #endregion

    #region Events

    public HookResult OnWeaponCanAcquire(DynamicHook hook)
    {
        Log.Debug("OnWeaponCanAcquire");

        var acquireMethod = hook.GetParam<AcquireMethod>(2);
        if (acquireMethod == AcquireMethod.PickUp)
        {
            return HookResult.Continue;
        }

        if (Helpers.IsWarmup())
        {
            return HookResult.Continue;
        }

        if (IsAllocatingForRound)
        {
            Log.Debug("Skipping OnWeaponCanAcquire because we're allocating for round");
            return HookResult.Continue;
        }

        HookResult RetStop()
        {
            hook.SetReturn(
                acquireMethod != AcquireMethod.PickUp
                    ? AcquireResult.AlreadyOwned
                    : AcquireResult.InvalidItem
            );

            return HookResult.Stop;
        }

        var player = hook.GetParam<CCSPlayer_ItemServices>(0).Pawn.Value.Controller.Value?.As<CCSPlayerController>();
        if (player is null || !player.IsValid || !player.PawnIsAlive)
        {
            Log.Debug($"Invalid player controller {player} {player?.IsValid} {player?.PawnIsAlive}");
            return HookResult.Continue;
        }

        // Identify the item from its definition index. The GetCSWeaponDataFromKey
        // native is only a guarded fallback: its signature regularly breaks on CS2
        // updates and used to crash this hook with "Invalid function pointer".
        var definitionIndex = hook.GetParam<CEconItemView>(1).ItemDefinitionIndex;
        CsItem? itemLookup = WeaponHelpers.GetItemFromDefinitionIndex(definitionIndex);
        if (itemLookup is null && CustomFunctions is not null)
        {
            try
            {
                var weaponData = CustomFunctions.GetCSWeaponDataFromKeyFunc?.Invoke(-1,
                    definitionIndex.ToString());
                if (weaponData is not null)
                {
                    itemLookup = Utils.ToEnum<CsItem>(weaponData.Name);
                }
            }
            catch (Exception e)
            {
                Log.Warn($"GetCSWeaponDataFromKey failed for def index {definitionIndex}: {e.Message}");
            }
        }

        if (itemLookup is null)
        {
            Log.Warn($"Unknown item definition index {definitionIndex}");
            return HookResult.Continue;
        }

        var team = player.Team;
        var item = itemLookup.Value;

        if (item is CsItem.KnifeT or CsItem.KnifeCT)
        {
            return HookResult.Continue;
        }

        if (item is CsItem.Taser)
        {
            // Zeus can always be "bought"; its cost is refunded in OnPostItemPurchase.
            return HookResult.Continue;
        }

        if (!WeaponHelpers.IsUsableWeapon(item))
        {
            return RetStop();
        }

        // Use the player's effective round type: on a force-buy round the
        // full-buying team may buy full-buy weapons.
        var effectiveRoundType = RoundTypeManager.Instance.GetCurrentEffectiveRoundType(team);

        var isPreferred = WeaponHelpers.IsPreferred(team, item);
        var purchasedAllocationType = effectiveRoundType is not null
            ? WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(
                effectiveRoundType, team, item
            )
            : null;
        var isValidAllocation = WeaponHelpers.IsAllocationTypeValidForRound(purchasedAllocationType,
            effectiveRoundType);

        if (
            !isPreferred &&
            isValidAllocation &&
            purchasedAllocationType is not null
        )
        {
            if (Helpers.IsWeaponAllocationAllowed())
            {
                return HookResult.Continue;
            }

            // The buy window is closed: save the choice as a preference for the next round instead.
            var playerId = Helpers.GetSteamId(player);
            if (playerId != 0)
            {
                Queries.SetWeaponPreferenceForUser(playerId, team, purchasedAllocationType.Value, item);
                Helpers.WriteNewlineDelimited(
                    Translator.Instance["weapon_preference.buy_window_closed"],
                    player.PrintToChat
                );
            }

            return RetStop();
        }

        // Weapon locked because of the round type: tell the buyer why nothing happened.
        if (!isPreferred && !isValidAllocation && WeaponHelpers.IsWeapon(item) && effectiveRoundType is not null)
        {
            Helpers.WriteNewlineDelimited(
                Translator.Instance[
                    "weapon_preference.locked_this_round",
                    item,
                    RoundTypeHelpers.TranslateRoundTypeName(effectiveRoundType.Value)],
                player.PrintToChat
            );
        }

        return RetStop();
    }

    public HookResult OnPostItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (Helpers.IsWarmup() || !Helpers.PlayerIsValid(player) || !player.PlayerPawn.IsValid)
        {
            return HookResult.Continue;
        }

        var item = Utils.ToEnum<CsItem>(@event.Weapon);
        var team = player.Team;
        var playerId = Helpers.GetSteamId(player);

        // Zeus is free: refund the purchase and keep the weapon.
        if (item is CsItem.Taser)
        {
            RefundZeusPurchase(player);
            return HookResult.Continue;
        }

        var isPreferred = WeaponHelpers.IsPreferred(team, item);

        var currentEffectiveRoundType = RoundTypeManager.Instance.GetCurrentEffectiveRoundType(team);

        var purchasedAllocationType = currentEffectiveRoundType is not null
            ? WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(
                currentEffectiveRoundType, team, item
            )
            : null;

        var isValidAllocation = WeaponHelpers.IsAllocationTypeValidForRound(purchasedAllocationType,
            currentEffectiveRoundType) && WeaponHelpers.IsUsableWeapon(item);

        Log.Debug($"item {item} team {team} player {playerId}");
        Log.Debug($"weapon alloc {purchasedAllocationType} valid? {isValidAllocation}");
        Log.Debug($"Preferred? {isPreferred}");

        if (
            Helpers.IsWeaponAllocationAllowed() &&
            // Preferred weapons are treated like un-buy-able weapons, but at the end we'll set the user preference
            !isPreferred &&
            isValidAllocation &&
            // redundant, just for null checker
            purchasedAllocationType is not null
        )
        {
            Queries.SetWeaponPreferenceForUser(
                playerId,
                team,
                purchasedAllocationType.Value,
                item
            );
            var slotType = WeaponHelpers.GetSlotTypeForItem(item);
            if (slotType is not null)
            {
                SetPlayerRoundAllocation(player, slotType.Value, item);
            }
            else
            {
                Log.Debug($"WARN: No slot for {item}");
            }

            // Economy: buying a non-default pistol during a pistol round costs your kevlar.
            if (currentEffectiveRoundType is not null)
            {
                ApplyPistolRoundEconomy(player, currentEffectiveRoundType.Value, purchasedAllocationType.Value, item);
            }

            // Keep the buy menu's greyed-out state stable after spending.
            TopUpRoundMoney(player);
        }
        else
        {
            var removedAnyWeapons = Helpers.RemoveWeapons(player,
                i =>
                {
                    if (!WeaponHelpers.IsWeapon(i))
                    {
                        return i == item;
                    }

                    if (currentEffectiveRoundType is null)
                    {
                        return true;
                    }

                    var at = WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(
                        currentEffectiveRoundType, team, i);
                    Log.Trace($"at: {at}");
                    return at is null || at == purchasedAllocationType;
                });
            Log.Debug($"Removed {item}? {removedAnyWeapons}");

            var replacementSlot = currentEffectiveRoundType == RoundType.Pistol
                ? ItemSlotType.Secondary
                : ItemSlotType.Primary;

            var replacedWeapon = false;
            var slotToSelect = WeaponHelpers.GetSlotNameForSlotType(replacementSlot);
            if (removedAnyWeapons && currentEffectiveRoundType is not null &&
                WeaponHelpers.IsWeapon(item))
            {
                var replacementAllocationType =
                    WeaponHelpers.GetReplacementWeaponAllocationTypeForWeapon(currentEffectiveRoundType);
                Log.Debug($"Replacement allocation type {replacementAllocationType}");
                if (replacementAllocationType is not null)
                {
                    var replacementItem = GetPlayerRoundAllocation(player, replacementSlot);
                    Log.Debug($"Replacement item {replacementItem} for slot {replacementSlot}");
                    if (replacementItem is not null)
                    {
                        replacedWeapon = true;
                        AllocateItemsForPlayer(player, new List<CsItem>
                        {
                            replacementItem.Value
                        }, slotToSelect);
                    }
                }
            }

            if (!replacedWeapon)
            {
                _plugin.AddTimer(0.1f, () =>
                {
                    if (Helpers.PlayerIsValid(player) && player.UserId is not null)
                    {
                        NativeAPI.IssueClientCommand((int) player.UserId, slotToSelect);
                    }
                });
            }
        }

        var playerPos = player.PlayerPawn.Value?.AbsOrigin;

        var pEntity = new CEntityIdentity(EntitySystem.FirstActiveEntity);
        for (; pEntity is not null && pEntity.Handle != IntPtr.Zero; pEntity = pEntity.Next)
        {
            var p = Utilities.GetEntityFromIndex<CBasePlayerWeapon>((int) pEntity.EntityInstance.Index);
            if (
                !p.IsValid ||
                !p.DesignerName.StartsWith("weapon") ||
                p.DesignerName.Equals("weapon_c4") ||
                playerPos is null ||
                p.AbsOrigin is null
            )
            {
                continue;
            }

            // Weapons swapped out by a purchase get dropped by the game; sweep a
            // generous radius around the buyer so nothing stays on the ground.
            var distance = Helpers.GetVectorDistance(playerPos, p.AbsOrigin);
            if (distance < 128)
            {
                _plugin.AddTimer(.5f, () =>
                {
                    if (p.IsValid && !p.OwnerEntity.IsValid)
                    {
                        Log.Trace($"Removing {p.DesignerName}");
                        p.Remove();
                    }
                });
            }
        }

        if (isPreferred)
        {
            var itemName = Enum.GetName(item);
            if (itemName is not null)
            {
                var message = OnWeaponCommandHelper.Handle(
                    new List<string> {itemName},
                    Helpers.GetSteamId(player),
                    currentEffectiveRoundType,
                    team,
                    false,
                    out _
                );
                Helpers.WriteNewlineDelimited(message, player.PrintToChat);
            }
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Zeus is free: give the player their money back after buying it.
    /// </summary>
    private void RefundZeusPurchase(CCSPlayerController player)
    {
        const int zeusCost = 200;
        var moneyServices = player.InGameMoneyServices;
        if (moneyServices is null)
        {
            return;
        }

        moneyServices.Account += zeusCost;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");

        TopUpRoundMoney(player);
    }

    /// <summary>
    /// Sets a player's cash to the configured amount for their effective round
    /// type. Weapons above that amount render greyed out in the native buy menu,
    /// which is the only client-side "disabled" visual the game exposes.
    /// </summary>
    private void SetRoundTypeMoney(CCSPlayerController player)
    {
        if (!Configs.GetConfigData().AdjustMoneyToRoundType || !Helpers.PlayerIsValid(player))
        {
            return;
        }

        var effectiveRoundType = RoundTypeManager.Instance.GetCurrentEffectiveRoundType(player.Team);
        if (effectiveRoundType is null ||
            !Configs.GetConfigData().MoneyByRoundType.TryGetValue(effectiveRoundType.Value, out var amount))
        {
            return;
        }

        var moneyServices = player.InGameMoneyServices;
        if (moneyServices is null)
        {
            return;
        }

        moneyServices.Account = amount;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    /// <summary>
    /// Restores the round-type money shortly after a purchase so the buy menu's
    /// greyed-out state stays consistent for the rest of the buy window.
    /// </summary>
    private void TopUpRoundMoney(CCSPlayerController player)
    {
        _plugin.AddTimer(0.1f, () =>
        {
            if (Helpers.PlayerIsValid(player))
            {
                SetRoundTypeMoney(player);
            }
        });
    }

    private void HandleAllocateEvent()
    {
        IsAllocatingForRound = true;
        Log.Debug($"Handling allocate event");
        Server.ExecuteCommand("mp_max_armor 0");

        var menu = _allocatorMenuManager.GetMenu<VoteMenu>(MenuType.NextRoundVote);
        menu.GatherAndHandleVotes();

        var allPlayers = Utilities.GetPlayers()
            .Where(player => Helpers.PlayerIsValid(player) && player.Connected == PlayerConnectedState.Connected)
            .ToList();

        OnRoundPostStartHelper.Handle(
            allPlayers,
            Helpers.GetSteamId,
            Helpers.GetTeam,
            GiveDefuseKit,
            AllocateItemsForPlayer,
            Helpers.IsVip,
            out var currentRoundType
        );
        RoundTypeManager.Instance.SetCurrentRoundType(currentRoundType);
        RoundTypeManager.Instance.SetNextRoundTypeOverride(null);

        // Money defines what the native buy menu greys out; set it once the
        // round type (and force-buy team) is known.
        _plugin.AddTimer(0.3f, () =>
        {
            foreach (var player in Utilities.GetPlayers().Where(Helpers.PlayerIsValid))
            {
                SetRoundTypeMoney(player);
            }
        });

        switch (currentRoundType)
        {
            case RoundType.Pistol:
            {
                Server.ExecuteCommand("execifexists cs2-retakes/Pistol.cfg");
                break;
            }
            case RoundType.HalfBuy:
            {
                Server.ExecuteCommand("execifexists cs2-retakes/SmallBuy.cfg");
                break;
            }
            case RoundType.FullBuy:
            {
                Server.ExecuteCommand("execifexists cs2-retakes/FullBuy.cfg");
                break;
            }
        }

        if (Configs.GetConfigData().EnableRoundTypeAnnouncement)
        {
            var roundType = RoundTypeManager.Instance.GetCurrentRoundType()!.Value;
            var roundTypeName = RoundTypeHelpers.TranslateRoundTypeName(roundType);
            var message = Translator.Instance["announcement.roundtype", roundTypeName];
            Server.PrintToChatAll($"{MessagePrefix}{message}");

            var forceBuyTeam = RoundTypeManager.Instance.ForceBuyTeam;
            if (forceBuyTeam is not null)
            {
                var forceBuyMessage = Translator.Instance[
                    "announcement.forcebuy_team",
                    Utils.TeamString(forceBuyTeam.Value)
                ];
                Server.PrintToChatAll($"{MessagePrefix}{forceBuyMessage}");
            }

            if (Configs.GetConfigData().EnableRoundTypeAnnouncementCenter)
            {
                foreach (var player in allPlayers)
                {
                    player.PrintToCenter(
                        $"{MessagePrefix}{Translator.Instance["center.announcement.roundtype", roundTypeName]}");
                }
            }
        }

        _plugin.AddTimer(.5f, () =>
        {
            Log.Debug("Turning off round allocation");
            IsAllocatingForRound = false;
        });
    }

    public void OnTick()
    {
        if (!string.IsNullOrEmpty(Configs.GetConfigData().InGameGunMenuCenterCommands))
        {
            _advancedGunMenu.OnTick();
        }

        if (_announceBombsite)
        {
            var playerEntities = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller");
            var countct = Utilities.GetPlayers()
                .Count(p => p.TeamNum == (int) CsTeam.CounterTerrorist && p.PawnIsAlive && !p.IsHLTV);
            var countt = Utilities.GetPlayers()
                .Count(p => p.TeamNum == (int) CsTeam.Terrorist && p.PawnIsAlive && !p.IsHLTV);
            string image = _bombsite == "A" ? Translator.Instance["BombSite.A"] :
                _bombsite == "B" ? Translator.Instance["BombSite.B"] : "";
            foreach (var player in playerEntities)
            {
                if (!player.IsValid || !player.PawnIsAlive || player.IsBot || player.IsHLTV) continue;

                if (player.TeamNum == (byte) CsTeam.Terrorist &&
                    !Configs.GetConfigData().BombSiteAnnouncementCenterToCTOnly)
                {
                    StringBuilder builder = new StringBuilder();
                    builder.AppendFormat(_plugin.Localizer["T.Message"], _bombsite, image, countt, countct);
                    var centerhtml = builder.ToString();
                    player.PrintToCenterHtml(centerhtml);
                }
                else if (player.TeamNum == (byte) CsTeam.CounterTerrorist)
                {
                    StringBuilder builder = new StringBuilder();
                    builder.AppendFormat(_plugin.Localizer["CT.Message"], _bombsite, image, countt, countct);
                    var centerhtml = builder.ToString();
                    player.PrintToCenterHtml(centerhtml);
                }
            }
        }
    }

    public HookResult OnEventBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;

        if (Configs.GetConfigData().DisableDefaultBombPlantedCenterMessage)
        {
            info.DontBroadcast = true;
        }

        if (Configs.GetConfigData().ForceCloseBombSiteAnnouncementCenterOnPlant)
        {
            _bombsite = "";
            _announceBombsite = false;
        }

        return HookResult.Continue;
    }

    public HookResult OnEventRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;
        _bombsiteAnnounceOneTime = false;
        return HookResult.Continue;
    }

    public HookResult OnEventRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null || Helpers.IsWarmup()) return HookResult.Continue;
        // The weapon change window starts counting from the moment the round goes live.
        RoundTypeManager.Instance.SetRoundLive();
        return HookResult.Continue;
    }

    public HookResult OnEventRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null) return HookResult.Continue;
        _bombsite = "";
        _announceBombsite = false;
        return HookResult.Continue;
    }

    public HookResult OnEventEnterBombzone(EventEnterBombzone @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (@event == null || Helpers.IsWarmup() || _bombsiteAnnounceOneTime) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid || player.TeamNum != (byte) CsTeam.Terrorist) return HookResult.Continue;

        var playerPawn = player.PlayerPawn;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (playerPawn == null || !playerPawn.IsValid) return HookResult.Continue;

        var playerPosition = playerPawn.Value!.AbsOrigin;

        foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBombTarget>("info_bomb_target"))
        {
            var entityPosition = entity.AbsOrigin;
            if (entityPosition != null)
            {
                var distanceVector = playerPosition! - entityPosition;
                var distance = distanceVector.Length();
                float thresholdDistance = 400.0f;

                if (distance <= thresholdDistance)
                {
                    if (entity.DesignerName == "info_bomb_target_hint_A")
                    {
                        _bombsite = "A";
                        if (Configs.GetConfigData().EnableBombSiteAnnouncementCenter)
                        {
                            Server.NextFrame(() =>
                            {
                                _plugin.AddTimer(Configs.GetConfigData().BombSiteAnnouncementCenterDelay, () =>
                                {
                                    _bombsiteAnnounceOneTime = true;
                                    _announceBombsite = true;
                                    _plugin.AddTimer(Configs.GetConfigData().BombSiteAnnouncementCenterShowTimer, () =>
                                    {
                                        _bombsite = "";
                                        _announceBombsite = false;
                                    }, TimerFlags.STOP_ON_MAPCHANGE);
                                }, TimerFlags.STOP_ON_MAPCHANGE);
                            });
                        }

                        if (Configs.GetConfigData().EnableBombSiteAnnouncementChat)
                        {
                            Server.PrintToChatAll(_plugin.Localizer["chatAsite.line1"]);
                            Server.PrintToChatAll(_plugin.Localizer["chatAsite.line2"]);
                            Server.PrintToChatAll(_plugin.Localizer["chatAsite.line3"]);
                            Server.PrintToChatAll(_plugin.Localizer["chatAsite.line4"]);
                            Server.PrintToChatAll(_plugin.Localizer["chatAsite.line5"]);
                            Server.PrintToChatAll(_plugin.Localizer["chatAsite.line6"]);
                        }

                        break;
                    }
                    else if (entity.DesignerName == "info_bomb_target_hint_B")
                    {
                        _bombsite = "B";
                        if (Configs.GetConfigData().EnableBombSiteAnnouncementCenter)
                        {
                            Server.NextFrame(() =>
                            {
                                _plugin.AddTimer(Configs.GetConfigData().BombSiteAnnouncementCenterDelay, () =>
                                {
                                    _bombsiteAnnounceOneTime = true;
                                    _announceBombsite = true;
                                    _plugin.AddTimer(Configs.GetConfigData().BombSiteAnnouncementCenterShowTimer, () =>
                                    {
                                        _bombsite = "";
                                        _announceBombsite = false;
                                    }, TimerFlags.STOP_ON_MAPCHANGE);
                                }, TimerFlags.STOP_ON_MAPCHANGE);
                            });
                        }

                        if (Configs.GetConfigData().EnableBombSiteAnnouncementChat)
                        {
                            Server.PrintToChatAll(_plugin.Localizer["chatBsite.line1"]);
                            Server.PrintToChatAll(_plugin.Localizer["chatBsite.line2"]);
                            Server.PrintToChatAll(_plugin.Localizer["chatBsite.line3"]);
                            Server.PrintToChatAll(_plugin.Localizer["chatBsite.line4"]);
                            Server.PrintToChatAll(_plugin.Localizer["chatBsite.line5"]);
                            Server.PrintToChatAll(_plugin.Localizer["chatBsite.line6"]);
                        }

                        break;
                    }
                }
            }
        }

        return HookResult.Continue;
    }

    public HookResult OnEventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (!string.IsNullOrEmpty(Configs.GetConfigData().InGameGunMenuCenterCommands))
        {
            _advancedGunMenu.OnEventPlayerDisconnect(@event, info);
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Chat trigger handling ("say"/"say_team" Post listener — replaces the
    /// removed EventPlayerChat game event).
    /// </summary>
    public HookResult OnSayCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null || !player.IsValid)
        {
            return HookResult.Continue;
        }

        var message = commandInfo.GetArg(1).Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return HookResult.Continue;
        }

        if (!string.IsNullOrEmpty(Configs.GetConfigData().InGameGunMenuCenterCommands))
        {
            _advancedGunMenu.HandleChatMessage(player, message);
        }

        if (!string.IsNullOrEmpty(Configs.GetConfigData().InGameGunMenuChatCommands))
        {
            string[] chatMenuCommands = Configs.GetConfigData().InGameGunMenuChatCommands.Split(',');

            if (chatMenuCommands.Any(cmd => cmd.Equals(message, StringComparison.OrdinalIgnoreCase)))
            {
                _allocatorMenuManager.OpenMenuForPlayer(player, MenuType.Guns);
            }
        }

        return HookResult.Continue;
    }

    public HookResult OnEventRoundAnnounceWarmup(EventRoundAnnounceWarmup @event, GameEventInfo info)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (!Configs.GetConfigData().ResetStateOnGameRestart || @event == null) return HookResult.Continue;
        ResetState();
        return HookResult.Continue;
    }

    #endregion

    #region Helpers

    private void SetPlayerRoundAllocation(CCSPlayerController player, ItemSlotType slotType, CsItem item)
    {
        if (!_allocatedPlayerItems.TryGetValue(player, out _))
        {
            _allocatedPlayerItems[player] = new();
        }

        _allocatedPlayerItems[player][slotType] = item;
        Log.Trace($"Round allocation for player {player.Slot} {slotType} {item}");
    }

    private CsItem? GetPlayerRoundAllocation(CCSPlayerController player, ItemSlotType? slotType)
    {
        if (slotType is null || !_allocatedPlayerItems.TryGetValue(player, out var playerItems))
        {
            return null;
        }

        if (playerItems.TryGetValue(slotType.Value, out var localReplacementItem))
        {
            return localReplacementItem;
        }

        return null;
    }

    private void AllocateItemsForPlayer(CCSPlayerController player, ICollection<CsItem> items, string? slotToSelect)
    {
        Log.Trace($"Allocating items: {string.Join(",", items)}; selecting slot {slotToSelect}");

        _plugin.AddTimer(0.1f, () =>
        {
            if (!Helpers.PlayerIsValid(player))
            {
                Log.Trace("Player is not valid when allocating item");
                return;
            }

            foreach (var item in items)
            {
                string? itemString = EnumUtils.GetEnumMemberAttributeValue(item);
                if (string.IsNullOrWhiteSpace(itemString))
                {
                    continue;
                }

                if (Configs.GetConfigData().CapabilityWeaponPaints && CustomFunctions != null &&
                    CustomFunctions.PlayerGiveNamedItemEnabled())
                {
                    CustomFunctions?.PlayerGiveNamedItem(player, itemString);
                }
                else
                {
                    player.GiveNamedItem(itemString);
                }

                var slotType = WeaponHelpers.GetSlotTypeForItem(item);
                if (slotType is not null)
                {
                    SetPlayerRoundAllocation(player, slotType.Value, item);
                }
            }

            if (slotToSelect is not null)
            {
                _plugin.AddTimer(0.1f, () =>
                {
                    if (Helpers.PlayerIsValid(player) && player.UserId is not null)
                    {
                        NativeAPI.IssueClientCommand((int) player.UserId, slotToSelect);
                    }
                });
            }
        });
    }

    private void GiveDefuseKit(CCSPlayerController player)
    {
        _plugin.AddTimer(0.1f, () =>
        {
            if (!Helpers.PlayerIsValid(player) || !player.PlayerPawn.IsValid || player.PlayerPawn.Value is null ||
                !player.PlayerPawn.Value.IsValid || player.PlayerPawn.Value?.ItemServices?.Handle is null)
            {
                Log.Trace($"Player is not valid when giving defuse kit");
                return;
            }

            var itemServices = new CCSPlayer_ItemServices(player.PlayerPawn.Value.ItemServices.Handle);
            itemServices.HasDefuser = true;
        });
    }

    #endregion
}
