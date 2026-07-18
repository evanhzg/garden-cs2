using System.Globalization;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using GardenRetakes.Core.Admin;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Managers;
using RetakesPlugin.Utils;
using AllocConfigs = RetakesAllocatorCore.Config.Configs;
using RankConfigs = GardenRankingsCore.Config.Configs;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// Live server-config menu (<c>!gmenu</c> / <c>!config</c>). One in-game menu to
/// flip the settings admins change most: friendly fire, force-camera, freeze/round
/// time, buy-anywhere, half-buy &amp; pistol frequency, auto-ranked, team scramble
/// and competitive availability.
///
/// SETUP CONSISTENCY BETWEEN MAPS: the cvar-backed toggles are stored in
/// GardenSettings.ServerControl (saved to the plugin config) and re-applied on
/// every map start, so a setup survives map changes — unlike a bare cvar typed in
/// console. The half-buy / ranked / scramble toggles are written straight into the
/// allocator and rankings configs, which those systems already read every map.
/// </summary>
public class ServerControlModule : IGardenModule
{
    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly AdminModule _admin;

    private static readonly int[] FreezeTimes = { 0, 2, 5, 7, 10, 15 };
    private static readonly double[] RoundTimes = { 1.15, 1.92, 3, 5 };

    private static readonly string[] HalfBuyNames = { "Off", "Rare", "Normal", "Frequent" };
    private static readonly int[] HalfBuyPcts = { 0, 12, 25, 45 };
    private static readonly string[] PistolNames = { "None", "Rare", "Normal", "Often" };
    private static readonly int[] PistolPcts = { 0, 8, 15, 30 };

    public string Name => "ServerControl";
    public bool Enabled => _host.Settings.ServerControl.Enabled;

    public ServerControlModule(RetakesPlugin plugin, GardenHost host, AdminModule admin)
    {
        _plugin = plugin;
        _host = host;
        _admin = admin;
    }

    public void Load(bool hotReload)
    {
        _plugin.AddCommand("css_gmenu", "Open the Garden server-config menu.", OnMenuCommand);
        _plugin.AddCommand("css_config", "Open the Garden server-config menu.", OnMenuCommand);
        _plugin.AddCommand("css_gsettings", "Open the Garden server-config menu.", OnMenuCommand);
    }

    public void OnMapStart(string mapName)
    {
        if (!Enabled)
        {
            return;
        }

        // Re-apply after the rankings ModeCvars pass (which also runs on map start)
        // so our chosen values win and the setup stays consistent across maps.
        _plugin.AddTimer(2.0f, ApplyCvars);
    }

    public void Unload() { }

    private string Prefix => _plugin.Localizer["garden.prefix"];

    // ---------- command ----------

    private void OnMenuCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player is null)
        {
            info.ReplyToCommand($"{Prefix} The config menu is in-game only (use !gconfig from console).");
            return;
        }

        if (!_admin.Require(player, info, AdminLevel.Admin))
        {
            return;
        }

        OpenMenu(player);
    }

    // ---------- menu ----------

    private void OpenMenu(CCSPlayerController player)
    {
        var s = _host.Settings.ServerControl;
        var menu = new CenterHtmlMenu("<font color='#c084fc'>Garden Server Config</font>", _plugin)
        {
            ExitButton = true,
        };

        // --- Gameplay cvars (map-persistent via OnMapStart) ---
        menu.AddMenuOption($"Friendly Fire (TK): {OnOff(s.FriendlyFire)}", (p, _) =>
        {
            s.FriendlyFire = !s.FriendlyFire;
            AfterCvarChange(p, $"Friendly fire {(s.FriendlyFire ? "ON" : "OFF")}");
        });

        menu.AddMenuOption($"Force Camera: {Val(s.ForceCamera ? "Team only" : "Free")}", (p, _) =>
        {
            s.ForceCamera = !s.ForceCamera;
            AfterCvarChange(p, $"Force camera {(s.ForceCamera ? "team-only" : "free")}");
        });

        menu.AddMenuOption($"Freeze Time: {Val($"{s.FreezeTime}s")}", (p, _) =>
        {
            s.FreezeTime = Cycle(FreezeTimes, s.FreezeTime);
            AfterCvarChange(p, $"Freeze time {s.FreezeTime}s");
        });

        menu.AddMenuOption($"Round Time: {Val($"{s.RoundTimeMinutes:0.##}min")}", (p, _) =>
        {
            s.RoundTimeMinutes = Cycle(RoundTimes, s.RoundTimeMinutes);
            AfterCvarChange(p, $"Round time {s.RoundTimeMinutes:0.##}min");
        });

        menu.AddMenuOption($"Buy Anywhere: {OnOff(s.BuyAnywhere)}", (p, _) =>
        {
            s.BuyAnywhere = !s.BuyAnywhere;
            AfterCvarChange(p, $"Buy anywhere {(s.BuyAnywhere ? "ON" : "OFF")}");
        });

        menu.AddMenuOption($"Infinite Ammo: {OnOff(s.InfiniteAmmo)}", (p, _) =>
        {
            s.InfiniteAmmo = !s.InfiniteAmmo;
            AfterCvarChange(p, $"Infinite ammo {(s.InfiniteAmmo ? "ON" : "OFF")}");
        });

        // --- Allocator round economy ---
        if (AllocAvailable())
        {
            var cfg = AllocConfigs.GetConfigData();
            var half = cfg.RoundTypePercentages.GetValueOrDefault(RoundType.HalfBuy);
            var pistol = cfg.RoundTypePercentages.GetValueOrDefault(RoundType.Pistol);

            menu.AddMenuOption($"Half-Buy Frequency: {Val(NearestName(HalfBuyPcts, HalfBuyNames, half))}", (p, _) =>
            {
                var next = HalfBuyPcts[(NearestIndex(HalfBuyPcts, half) + 1) % HalfBuyPcts.Length];
                SetRoundEconomy(pistol, next, p, $"Half-buy {NearestName(HalfBuyPcts, HalfBuyNames, next)}");
            });

            menu.AddMenuOption($"Pistol Frequency: {Val(NearestName(PistolPcts, PistolNames, pistol))}", (p, _) =>
            {
                var next = PistolPcts[(NearestIndex(PistolPcts, pistol) + 1) % PistolPcts.Length];
                SetRoundEconomy(next, half, p, $"Pistol rounds {NearestName(PistolPcts, PistolNames, next)}");
            });
        }
        else
        {
            menu.AddMenuOption("Round economy: (allocator off)", (_, _) => { }, disabled: true);
        }

        // --- Rankings / mode ---
        if (RankAvailable())
        {
            var rc = RankConfigs.GetConfigData();

            menu.AddMenuOption($"Auto-Ranked: {OnOff(rc.Ranked.AutoActivate)}", (p, _) =>
            {
                rc.Ranked.AutoActivate = !rc.Ranked.AutoActivate;
                AfterRankChange(p, $"Auto-ranked {(rc.Ranked.AutoActivate ? "ON" : "OFF")}");
            });

            menu.AddMenuOption($"Scramble Each Round: {OnOff(rc.ModeCvars.ScrambleTeamsEachRound)}", (p, _) =>
            {
                rc.ModeCvars.ScrambleTeamsEachRound = !rc.ModeCvars.ScrambleTeamsEachRound;
                AfterRankChange(p, $"Scramble {(rc.ModeCvars.ScrambleTeamsEachRound ? "ON" : "OFF")}");
            });

            var crOn = rc.Competitive.AllowedTeamSizes.Count > 0;
            menu.AddMenuOption($"Competitive (2v2/3v3): {OnOff(crOn)}", (p, _) =>
            {
                rc.Competitive.AllowedTeamSizes = crOn ? new List<int>() : new List<int> { 2, 3 };
                AfterRankChange(p, $"Competitive {(crOn ? "disabled" : "enabled")}");
            });
        }
        else
        {
            menu.AddMenuOption("Ranked/scramble: (rankings off)", (_, _) => { }, disabled: true);
        }

        MenuManager.OpenCenterHtmlMenu(_plugin, player, menu);
    }

    // ---------- apply + persist ----------

    private void ApplyCvars()
    {
        var s = _host.Settings.ServerControl;
        var inv = CultureInfo.InvariantCulture;
        Server.ExecuteCommand($"mp_friendlyfire {(s.FriendlyFire ? 1 : 0)}");
        Server.ExecuteCommand($"mp_forcecamera {(s.ForceCamera ? 1 : 0)}");
        Server.ExecuteCommand($"mp_freezetime {s.FreezeTime}");
        Server.ExecuteCommand($"mp_roundtime_defuse {s.RoundTimeMinutes.ToString(inv)}");
        Server.ExecuteCommand($"mp_roundtime {s.RoundTimeMinutes.ToString(inv)}");
        Server.ExecuteCommand($"mp_buy_anywhere {(s.BuyAnywhere ? 1 : 0)}");
        Server.ExecuteCommand($"sv_infinite_ammo {(s.InfiniteAmmo ? 2 : 0)}");
    }

    private void AfterCvarChange(CCSPlayerController player, string what)
    {
        ApplyCvars();
        SaveCssConfig();
        _admin.LogAction(player, "gmenu", detail: what);
        player.PrintToChat($"{Prefix} {what}.");
        OpenMenu(player); // refresh with new values
    }

    private void SetRoundEconomy(int pistolPct, int halfBuyPct, CCSPlayerController player, string what)
    {
        if (!AllocAvailable())
        {
            return;
        }

        var cfg = AllocConfigs.GetConfigData();
        pistolPct = Math.Clamp(pistolPct, 0, 100);
        halfBuyPct = Math.Clamp(halfBuyPct, 0, 100 - pistolPct);
        cfg.RoundTypePercentages[RoundType.Pistol] = pistolPct;
        cfg.RoundTypePercentages[RoundType.HalfBuy] = halfBuyPct;
        cfg.RoundTypePercentages[RoundType.FullBuy] = 100 - pistolPct - halfBuyPct;

        try
        {
            AllocConfigs.Save();
            RoundTypeManager.Instance.Initialize(); // apply the new distribution live
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/ServerControl", $"Round economy save failed: {ex.Message}");
        }

        _admin.LogAction(player, "gmenu", detail: what);
        player.PrintToChat($"{Prefix} {what}.");
        OpenMenu(player);
    }

    private void AfterRankChange(CCSPlayerController player, string what)
    {
        try
        {
            RankConfigs.Save();
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/ServerControl", $"Rankings save failed: {ex.Message}");
        }

        _admin.LogAction(player, "gmenu", detail: what);
        player.PrintToChat($"{Prefix} {what}.");
        OpenMenu(player);
    }

    /// <summary>Writes the live CSS config back to RetakesPlugin.json (same path CSS loads from).</summary>
    private void SaveCssConfig()
    {
        try
        {
            var configDir = Path.GetFullPath(Path.Combine(
                _plugin.ModuleDirectory, "..", "..", "configs", "plugins", "RetakesPlugin"));
            Directory.CreateDirectory(configDir);
            File.WriteAllText(Path.Combine(configDir, "RetakesPlugin.json"),
                JsonSerializer.Serialize(_plugin.Config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/ServerControl", $"CSS config save failed: {ex.Message}");
        }
    }

    // ---------- helpers ----------

    private bool AllocAvailable() => _host.Settings.Allocator.Enabled && AllocConfigs.IsLoaded();
    private bool RankAvailable() => _host.Settings.Rankings.Enabled && RankConfigs.IsLoaded();

    private static string OnOff(bool on) =>
        on ? "<font color='#7CFC00'>ON</font>" : "<font color='#FF6B6B'>OFF</font>";

    private static string Val(string v) => $"<font color='#c084fc'>{v}</font>";

    private static int Cycle(int[] options, int current)
    {
        var idx = Array.IndexOf(options, current);
        return options[(idx < 0 ? 0 : idx + 1) % options.Length];
    }

    private static double Cycle(double[] options, double current)
    {
        var idx = Array.FindIndex(options, o => Math.Abs(o - current) < 0.001);
        return options[(idx < 0 ? 0 : idx + 1) % options.Length];
    }

    private static int NearestIndex(int[] pcts, int value)
    {
        var best = 0;
        var bestDiff = int.MaxValue;
        for (var i = 0; i < pcts.Length; i++)
        {
            var d = Math.Abs(pcts[i] - value);
            if (d < bestDiff) { bestDiff = d; best = i; }
        }
        return best;
    }

    private static string NearestName(int[] pcts, string[] names, int value) => names[NearestIndex(pcts, value)];
}
