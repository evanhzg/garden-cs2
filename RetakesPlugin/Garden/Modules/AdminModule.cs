using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using GardenRetakes.Core.Admin;
using RetakesPlugin.Utils;
using RankingsQueries = GardenRankingsCore.Db.Queries;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// Garden admin system (ROADMAP R3 basis).
///
/// Storage: garden_admins.json in the plugin directory + Owner bootstrap from
/// config (Garden.Admin.OwnerSteamIds). CSS @css/root holders are treated as
/// Owners too, so the system is usable before any config exists.
///
/// Commands (g-prefixed to avoid collisions with the legacy plugins while both
/// run; they take the short names when Garden-retakes becomes the only plugin):
///   css_gadmin add &lt;steamid|name&gt; &lt;owner|admin|mod&gt;   (Owner)
///   css_gadmin remove &lt;steamid|name&gt;                  (Owner)
///   css_gadmin list
///   css_gkick &lt;name&gt;                                  (Moderator+)
///   css_gmap &lt;map&gt;                                    (Moderator+)
///   css_gslay &lt;name&gt;                                  (Admin+)
///   css_grcon &lt;command...&gt;                            (Owner)
/// </summary>
public class AdminModule : IGardenModule
{
    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly AdminRegistry _registry = new();
    private string _storePath = "";

    // R3: DB persistence (shared rankings DB). JSON stays as bootstrap/fallback.
    private bool _dbSynced;

    public string Name => "Admin";
    public bool Enabled => true;

    public AdminModule(RetakesPlugin plugin, GardenHost host)
    {
        _plugin = plugin;
        _host = host;
    }

    public void Load(bool hotReload)
    {
        _storePath = Path.Combine(_plugin.ModuleDirectory, "garden_admins.json");
        _registry.BootstrapConfigOwners(_host.Settings.Admin.OwnerSteamIds);

        if (File.Exists(_storePath))
        {
            try
            {
                _registry.Load(File.ReadAllText(_storePath));
            }
            catch (Exception ex)
            {
                Logger.LogException("Garden/Admin", ex);
            }
        }

        _plugin.AddCommand("css_gadmin", "Garden admin management (add/remove/list).", OnAdminCommand);
        _plugin.AddCommand("css_gkick", "Kick a player.", OnKickCommand);
        _plugin.AddCommand("css_gslay", "Slay a player.", OnSlayCommand);
        _plugin.AddCommand("css_gmap", "Change the map.", OnMapCommand);
        _plugin.AddCommand("css_grcon", "Run a server command.", OnRconCommand);
        // W2: bans (DB-backed, enforced on connect by the rankings module).
        _plugin.AddCommand("css_gban", "Ban a player. Usage: !gban <name|steamid> [minutes] [reason...]", OnBanCommand);
        _plugin.AddCommand("css_gunban", "Unban a player. Usage: !gunban <steamid>", OnUnbanCommand);

        // R10: short aliases for after the legacy-plugin transition.
        if (_host.Settings.Admin.EnableShortAliases)
        {
            _plugin.AddCommand("css_admin", "Garden admin management (add/remove/list).", OnAdminCommand);
            _plugin.AddCommand("css_kick", "Kick a player.", OnKickCommand);
            _plugin.AddCommand("css_slay", "Slay a player.", OnSlayCommand);
            _plugin.AddCommand("css_map", "Change the map.", OnMapCommand);
            _plugin.AddCommand("css_rcon", "Run a server command.", OnRconCommand);
            _plugin.AddCommand("css_ban", "Ban a player.", OnBanCommand);
            _plugin.AddCommand("css_unban", "Unban a player.", OnUnbanCommand);
        }

        // R3: pull admins from the shared DB once the rankings module has it ready.
        _plugin.AddTimer(5.0f, TrySyncFromDb, TimerFlags.REPEAT);
    }

    private bool DbAvailable =>
        _dbSynced ||
        (_host.Settings.Rankings.Enabled && GardenRankingsCore.Config.Configs.IsLoaded());

    private void TrySyncFromDb()
    {
        if (_dbSynced || !DbAvailable)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var admins = RankingsQueries.GetGardenAdmins();
                Server.NextFrame(() =>
                {
                    if (_dbSynced)
                    {
                        return;
                    }

                    _registry.Import(admins.Select(a => new AdminEntry
                    {
                        SteamId = a.SteamId,
                        Name = a.Name,
                        Level = (AdminLevel) a.Level,
                        AddedBy = a.AddedBy,
                        AddedAtUtc = a.AddedAtUtc,
                    }));
                    _dbSynced = true;
                    Persist();
                    Logger.LogInfo("Garden/Admin", $"Admin registry synced from DB ({admins.Count} entries).");
                });
            }
            catch
            {
                // DB not initialized yet (rankings module still starting) — retried by the timer.
            }
        });
    }

    /// <summary>R3: writes every admin action to the shared DB audit log (best effort).</summary>
    public void LogAction(CCSPlayerController? actor, string action,
        ulong targetSteamId = 0, string targetName = "", string detail = "")
    {
        Logger.LogInfo("Garden/Admin",
            $"{actor?.PlayerName ?? "Console"} {action} {targetName}{(detail.Length > 0 ? $" ({detail})" : "")}");

        if (!_dbSynced && !DbAvailable)
        {
            return;
        }

        var actorId = actor?.SteamID ?? 0;
        var actorName = actor?.PlayerName ?? "Console";
        Task.Run(() =>
        {
            try
            {
                RankingsQueries.LogGardenAdminAction(actorId, actorName, action, targetSteamId, targetName, detail);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Garden/Admin", $"Failed to write admin log: {ex.Message}");
            }
        });
    }

    public void OnMapStart(string mapName) { }

    public void Unload() { }

    // ---------- permission helpers ----------

    /// <summary>Console counts as Owner; CSS @css/root counts as Owner (bootstrap path).</summary>
    public AdminLevel GetLevel(CCSPlayerController? player)
    {
        if (player == null)
        {
            return AdminLevel.Owner;
        }

        var level = _registry.GetLevel(player.SteamID);
        if (level < AdminLevel.Owner && AdminManager.PlayerHasPermissions(player, "@css/root"))
        {
            return AdminLevel.Owner;
        }

        return level;
    }

    public bool Require(CCSPlayerController? player, CommandInfo info, AdminLevel required)
    {
        if (GetLevel(player) >= required)
        {
            return true;
        }

        info.ReplyToCommand($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.no_permission"]}");
        return false;
    }

    // ---------- commands ----------

    private void OnAdminCommand(CCSPlayerController? player, CommandInfo info)
    {
        var prefix = _plugin.Localizer["garden.prefix"];
        var action = info.GetArg(1).ToLowerInvariant();

        switch (action)
        {
            case "list":
            {
                if (!Require(player, info, AdminLevel.Admin)) return;
                info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.admin.list_header"]}");
                foreach (var entry in _registry.All)
                {
                    info.ReplyToCommand($"{prefix} {entry.Level,-9} {entry.Name} ({entry.SteamId})");
                }
                return;
            }
            case "add":
            {
                if (!Require(player, info, AdminLevel.Owner)) return;
                ResolveTarget(info.GetArg(2), out var targetSteamId, out var targetName);
                if (targetSteamId == 0)
                {
                    info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.target_not_found", info.GetArg(2)]}");
                    return;
                }

                var level = info.GetArg(3).ToLowerInvariant() switch
                {
                    "owner" => AdminLevel.Owner,
                    "admin" or "" => AdminLevel.Admin,
                    "mod" or "moderator" => AdminLevel.Moderator,
                    _ => AdminLevel.None,
                };

                if (!_registry.TryAdd(player?.SteamID ?? 0, GetLevel(player), targetSteamId, targetName, level, out var error))
                {
                    info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.admin.error", error ?? "unknown"]}");
                    return;
                }

                Persist();
                if (DbAvailable)
                {
                    var actorId = player?.SteamID ?? 0;
                    var (dbTarget, dbName, dbLevel) = (targetSteamId, targetName, (int) level);
                    Task.Run(() =>
                    {
                        try { RankingsQueries.UpsertGardenAdmin(dbTarget, dbName, dbLevel, actorId); }
                        catch (Exception ex) { Logger.LogWarning("Garden/Admin", $"DB upsert failed: {ex.Message}"); }
                    });
                }

                LogAction(player, "admin_add", targetSteamId, targetName, level.ToString());
                info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.admin.added", targetName, level.ToString()]}");
                return;
            }
            case "remove":
            {
                if (!Require(player, info, AdminLevel.Owner)) return;
                ResolveTarget(info.GetArg(2), out var targetSteamId, out var targetName);
                if (targetSteamId == 0)
                {
                    info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.target_not_found", info.GetArg(2)]}");
                    return;
                }

                if (!_registry.TryRemove(GetLevel(player), targetSteamId, out var error))
                {
                    info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.admin.error", error ?? "unknown"]}");
                    return;
                }

                Persist();
                if (DbAvailable)
                {
                    var dbTarget = targetSteamId;
                    Task.Run(() =>
                    {
                        try { RankingsQueries.DeleteGardenAdmin(dbTarget); }
                        catch (Exception ex) { Logger.LogWarning("Garden/Admin", $"DB delete failed: {ex.Message}"); }
                    });
                }

                LogAction(player, "admin_remove", targetSteamId, targetName);
                info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.admin.removed", targetName]}");
                return;
            }
            default:
                info.ReplyToCommand($"{prefix} {_plugin.Localizer["garden.admin.usage"]}");
                return;
        }
    }

    private void OnKickCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Require(player, info, AdminLevel.Moderator)) return;

        var target = FindOnlinePlayer(info.GetArg(1));
        if (target?.UserId == null)
        {
            info.ReplyToCommand($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.target_not_found", info.GetArg(1)]}");
            return;
        }

        LogAction(player, "kick", target.SteamID, target.PlayerName);
        Server.ExecuteCommand($"kickid {target.UserId}");
        Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.kick.done", target.PlayerName]}");
    }

    private void OnSlayCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Require(player, info, AdminLevel.Admin)) return;

        var target = FindOnlinePlayer(info.GetArg(1));
        if (target == null || !PlayerHelper.HasAlivePawn(target))
        {
            info.ReplyToCommand($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.target_not_found", info.GetArg(1)]}");
            return;
        }

        LogAction(player, "slay", target.SteamID, target.PlayerName);
        target.PlayerPawn.Value?.CommitSuicide(explode: false, force: true);
        Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.slay.done", target.PlayerName]}");
    }

    private void OnMapCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Require(player, info, AdminLevel.Moderator)) return;

        var map = info.GetArg(1).ToLowerInvariant();
        if (map.Length == 0 || !map.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            info.ReplyToCommand($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.map.usage"]}");
            return;
        }

        LogAction(player, "map_change", detail: map);
        Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.map.changing", map]}");
        _plugin.AddTimer(1.0f, () => Server.ExecuteCommand($"changelevel {map}"));
    }

    /// <summary>W2: !gban &lt;name|steamid&gt; [minutes] [reason...] — 0/omitted minutes = permanent.</summary>
    private void OnBanCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Require(player, info, AdminLevel.Admin)) return;

        ResolveTarget(info.GetArg(1), out var targetSteamId, out var targetName);
        if (targetSteamId == 0)
        {
            info.ReplyToCommand($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.target_not_found", info.GetArg(1)]}");
            return;
        }

        var argIndex = 2;
        DateTime? expires = null;
        if (int.TryParse(info.GetArg(2), out var minutes))
        {
            argIndex = 3;
            if (minutes > 0)
            {
                expires = DateTime.UtcNow.AddMinutes(minutes);
            }
        }

        var reason = info.ArgCount > argIndex
            ? string.Join(' ', Enumerable.Range(argIndex, info.ArgCount - argIndex).Select(info.GetArg)).Trim()
            : "Banned by an admin";

        var actorId = player?.SteamID ?? 0;
        var (dbTarget, dbName, dbReason, dbExpires) = (targetSteamId, targetName, reason, expires);
        Task.Run(() =>
        {
            try { RankingsQueries.UpsertGardenBan(dbTarget, dbName, dbReason, actorId, dbExpires); }
            catch (Exception ex) { Logger.LogWarning("Garden/Admin", $"Ban write failed: {ex.Message}"); }
        });

        var online = FindOnlinePlayer(targetName) ?? Utilities.GetPlayers()
            .FirstOrDefault(p => PlayerHelper.IsValid(p) && p.SteamID == targetSteamId);
        if (online?.UserId is not null)
        {
            Server.ExecuteCommand($"kickid {online.UserId} Banned: {reason}");
        }

        LogAction(player, "ban", targetSteamId, targetName, reason);
        Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.ban.done",
            targetName, expires is null ? "∞" : $"{minutes}min"]}");
    }

    private void OnUnbanCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Require(player, info, AdminLevel.Admin)) return;

        if (!ulong.TryParse(info.GetArg(1), out var steamId) || steamId == 0)
        {
            info.ReplyToCommand($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.ban.unban_usage"]}");
            return;
        }

        Task.Run(() =>
        {
            try { RankingsQueries.DeleteGardenBan(steamId); }
            catch (Exception ex) { Logger.LogWarning("Garden/Admin", $"Unban failed: {ex.Message}"); }
        });

        LogAction(player, "unban", steamId, steamId.ToString());
        info.ReplyToCommand($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.ban.unbanned", steamId]}");
    }

    private void OnRconCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Require(player, info, AdminLevel.Owner)) return;

        var command = info.ArgString.Trim();
        if (command.Length == 0)
        {
            info.ReplyToCommand($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.rcon.usage"]}");
            return;
        }

        LogAction(player, "rcon", detail: command.Length > 250 ? command[..250] : command);
        Server.ExecuteCommand(command);
        info.ReplyToCommand($"{_plugin.Localizer["garden.prefix"]} {_plugin.Localizer["garden.rcon.done", command]}");
    }

    // ---------- helpers ----------

    private void Persist()
    {
        try
        {
            File.WriteAllText(_storePath, _registry.Serialize());
        }
        catch (Exception ex)
        {
            Logger.LogException("Garden/Admin", ex);
        }
    }

    /// <summary>Accepts a SteamID64 or a (partial) name of an online player.</summary>
    private CCSPlayerController? ResolveTarget(string arg, out ulong steamId, out string name)
    {
        steamId = 0;
        name = arg;

        if (ulong.TryParse(arg, out var parsed) && parsed > 76500000000000000)
        {
            steamId = parsed;
            var online = Utilities.GetPlayers().FirstOrDefault(p => PlayerHelper.IsValid(p) && p.SteamID == parsed);
            if (online != null)
            {
                name = online.PlayerName;
            }
            return online;
        }

        var target = FindOnlinePlayer(arg);
        if (target != null)
        {
            steamId = target.SteamID;
            name = target.PlayerName;
        }
        return target;
    }

    private static CCSPlayerController? FindOnlinePlayer(string namePart)
    {
        if (string.IsNullOrWhiteSpace(namePart))
        {
            return null;
        }

        return Utilities.GetPlayers()
            .Where(p => PlayerHelper.IsValid(p) && !p.IsBot)
            .FirstOrDefault(p => p.PlayerName.Contains(namePart, StringComparison.OrdinalIgnoreCase));
    }
}
