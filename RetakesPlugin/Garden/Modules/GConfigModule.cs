using System.Text.Json;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using GardenRetakes.Core.Admin;
using GardenRetakes.Core.Config;
using GardenRetakes.Core.GameModes;
using RetakesPlugin.Utils;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// In-game configuration (ROADMAP R2): !gconfig browses and edits every config
/// of the merged plugin with live apply + save.
///
///   !gconfig                              — list targets
///   !gconfig <target>                     — list sections/values (Admin)
///   !gconfig <target> <path>              — show one value/section (Admin)
///   !gconfig <target> <path> <value>      — set + apply + save (Owner)
///
/// Targets:
///   retakes   — the CSS plugin config (GameSettings, TeamSettings, ... incl. GardenSettings)
///   garden    — shortcut rooted at GardenSettings
///   allocator — config/config.json (RetakesAllocatorCore)
///   rankings  — config/rankings.json (GardenRankingsCore)
///
/// Most values are read per-use so they apply immediately; construction-time
/// values (queue sizes, spawn ratios, ...) apply on the next map — the command
/// says so. Collections stay file-only.
/// </summary>
public class GConfigModule : IGardenModule
{
    private static readonly string[] Targets = ["retakes", "garden", "allocator", "rankings"];

    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly AdminModule _admin;

    public string Name => "GConfig";
    public bool Enabled => true;

    public GConfigModule(RetakesPlugin plugin, GardenHost host, AdminModule admin)
    {
        _plugin = plugin;
        _host = host;
        _admin = admin;
    }

    public void Load(bool hotReload)
    {
        _plugin.AddCommand("css_gconfig", "Browse/edit the Garden configs in game.", OnGConfigCommand);
    }

    public void OnMapStart(string mapName) { }

    public void Unload() { }

    private string Prefix => _plugin.Localizer["garden.prefix"];

    private object? ResolveTargetRoot(string target) => target switch
    {
        "retakes" => _plugin.Config,
        "garden" => _plugin.Config.Garden,
        "allocator" when _host.Settings.Allocator.Enabled &&
                         RetakesAllocatorCore.Config.Configs.IsLoaded()
            => RetakesAllocatorCore.Config.Configs.GetConfigData(),
        "rankings" when _host.Settings.Rankings.Enabled &&
                        GardenRankingsCore.Config.Configs.IsLoaded()
            => GardenRankingsCore.Config.Configs.GetConfigData(),
        _ => null,
    };

    private void OnGConfigCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!_admin.Require(player, info, AdminLevel.Admin))
        {
            return;
        }

        var target = info.GetArg(1).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(target))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.gconfig.usage",
                string.Join(", ", Targets)]}");
            return;
        }

        if (!Targets.Contains(target))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.gconfig.unknown_target",
                target, string.Join(", ", Targets)]}");
            return;
        }

        var root = ResolveTargetRoot(target);
        if (root is null)
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.gconfig.target_unavailable", target]}");
            return;
        }

        var path = info.GetArg(2);
        // The value may contain spaces: everything after the path is the raw value.
        var value = info.ArgCount > 3
            ? string.Join(' ', Enumerable.Range(3, info.ArgCount - 3).Select(info.GetArg))
            : "";

        if (string.IsNullOrWhiteSpace(value))
        {
            // Read / list.
            if (!ConfigReflection.TryDescribe(root, path, out var lines, out var error))
            {
                info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.gconfig.error", error ?? "?"]}");
                return;
            }

            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.gconfig.header",
                target, string.IsNullOrWhiteSpace(path) ? "*" : path]}");
            foreach (var line in lines.Take(30))
            {
                info.ReplyToCommand($"{Prefix} {line}");
            }

            if (lines.Count > 30)
            {
                info.ReplyToCommand($"{Prefix} ... ({lines.Count - 30} more — narrow the path)");
            }

            return;
        }

        // Write: Owner only.
        if (!_admin.Require(player, info, AdminLevel.Owner))
        {
            return;
        }

        if (!ConfigReflection.TrySet(root, path, value, out var oldValue, out var setError))
        {
            info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.gconfig.error", setError ?? "?"]}");
            return;
        }

        ApplyAndSave(target);
        _admin.LogAction(player, "gconfig", detail: $"{target}.{path}: {oldValue} -> {value}");
        info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.gconfig.set",
            $"{target}.{path}", oldValue ?? "?", value]}");
        info.ReplyToCommand($"{Prefix} {_plugin.Localizer["garden.gconfig.apply_note"]}");
    }

    private void ApplyAndSave(string target)
    {
        switch (target)
        {
            case "allocator":
                RetakesAllocatorCore.Config.Configs.Save();
                break;
            case "rankings":
                GardenRankingsCore.Config.Configs.Save();
                break;
            default: // retakes / garden — the CSS plugin config
                SaveCssConfig();
                SyncGardenRuntime();
                break;
        }
    }

    /// <summary>
    /// Writes the live CSS config back to configs/plugins/RetakesPlugin/RetakesPlugin.json
    /// (CounterStrikeSharp's convention relative to the plugin directory).
    /// </summary>
    private void SaveCssConfig()
    {
        try
        {
            var configDir = Path.GetFullPath(Path.Combine(
                _plugin.ModuleDirectory, "..", "..", "configs", "plugins", "RetakesPlugin"));
            Directory.CreateDirectory(configDir);
            var configPath = Path.Combine(configDir, "RetakesPlugin.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(_plugin.Config,
                new JsonSerializerOptions { WriteIndented = true }));
            Logger.LogInfo("Garden/GConfig", $"CSS config saved to {configPath}");
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/GConfig", $"Failed to save CSS config: {ex.Message}");
        }
    }

    /// <summary>Re-applies GardenSettings values that were copied at construction time.</summary>
    private void SyncGardenRuntime()
    {
        _host.Modes.SmallServerMaxHumans = _host.Settings.SmallServer.MaxHumans;
        _host.Modes.SetSmallServerState(_host.Settings.SmallServer.Mode.Trim().ToLowerInvariant() switch
        {
            "on" => SmallServerState.ForcedOn,
            "off" => SmallServerState.ForcedOff,
            _ => SmallServerState.Auto,
        });
    }
}
