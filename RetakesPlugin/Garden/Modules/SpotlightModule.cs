using System.Drawing;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRetakes.Core.Admin;
using RetakesPlugin.Utils;

namespace RetakesPlugin.Garden.Modules;

/// <summary>
/// "Spotlight" — a fun module that keeps an eye on specific player(s) (default:
/// Damien / vz7y, the T-side rusher).
///
///  - PUSH ALERT: define map zones with !pushzone; when a watched player is
///    inside one during the first N seconds after freeze end, the CTs get
///    "⚠ Damien is pushing short!" (chat + center). Once per zone per round.
///  - GAG EFFECTS (admin): !reveal glows the target through walls for everyone
///    (timed), !nojump stops the target jumping for the round. Both can be set
///    to auto-apply every round via config (AutoReveal / AutoNoJump).
///
/// Zones live in spotlight_zones/&lt;map&gt;.json (center + radius sphere).
/// Everything is opt-in and reads config live so !gconfig tweaks apply at once.
/// </summary>
public class SpotlightModule : IGardenModule
{
    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly AdminModule _admin;

    private readonly List<SpotlightZone> _zones = [];
    private string _zonesPath = "";
    private DateTime _windowEnd = DateTime.MinValue;
    private readonly HashSet<string> _firedThisRound = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ulong> _revealed = [];
    // steamid -> the glow follower entities we spawned for that player (relay + glow).
    private readonly Dictionary<ulong, List<uint>> _glowEntities = [];
    private readonly HashSet<ulong> _noJump = [];

    private static readonly Color RevealColor = Color.FromArgb(255, 255, 45, 45);
    private const string FallbackModel = "characters/models/tm_phoenix/tm_phoenix.vmdl";

    public string Name => "Spotlight";
    public bool Enabled => Cfg.Enabled;

    public SpotlightModule(RetakesPlugin plugin, GardenHost host, AdminModule admin)
    {
        _plugin = plugin;
        _host = host;
        _admin = admin;
    }

    private SpotlightSettings Cfg => _host.Settings.Spotlight;
    private string Prefix => _plugin.Localizer["garden.prefix"];

    public void Load(bool hotReload)
    {
        _plugin.AddCommand("css_pushzone", "Spotlight push zones: add/del/list/show.", OnZoneCommand);
        _plugin.AddCommand("css_reveal", "Glow a player through walls for everyone. Usage: !reveal [player] [seconds]", OnRevealCommand);
        _plugin.AddCommand("css_nojump", "Toggle a player being unable to jump this round. Usage: !nojump [player]", OnNoJumpCommand);
        _plugin.AddCommand("css_spotlight", "Show Spotlight status.", OnStatusCommand);

        // Round window: freeze end starts the early-round alert window + auto gags.
        _plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnFreezeEnd);
        // New round: wipe last round's effects (autos are reapplied at freeze end).
        _plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);

        // Push detection — light poll (0.25s) that only does work inside the window.
        _plugin.AddTimer(0.25f, CheckPushes, TimerFlags.REPEAT);

        // No-jump: cancel any upward velocity for flagged targets each tick.
        _plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    public void OnMapStart(string mapName)
    {
        LoadZones(mapName);
        _firedThisRound.Clear();
        // Follower entities die with the old map; just drop our references.
        _glowEntities.Clear();
        _revealed.Clear();
        _noJump.Clear();
        _windowEnd = DateTime.MinValue;
    }

    public void Unload()
    {
        foreach (var steamId in _revealed.ToList())
        {
            RemoveGlow(steamId);
        }
        _revealed.Clear();
        _noJump.Clear();
    }

    // ---------- round lifecycle ----------

    private HookResult OnFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (!Cfg.Enabled)
        {
            return HookResult.Continue;
        }

        _windowEnd = DateTime.UtcNow.AddSeconds(Math.Max(0, Cfg.AlertWindowSeconds));
        _firedThisRound.Clear();

        // Auto gags: apply once the round is live (targets have a fresh pawn).
        foreach (var steamId in Cfg.Targets)
        {
            var player = FindOnline(steamId);
            if (player is null)
            {
                continue;
            }

            if (Cfg.AutoReveal)
            {
                _revealed.Add(steamId);
                ApplyGlow(player, steamId);
            }
            if (Cfg.AutoNoJump)
            {
                _noJump.Add(steamId);
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Clear effects from the previous round (autos come back at freeze end).
        foreach (var steamId in _revealed.ToList())
        {
            RemoveGlow(steamId);
        }
        _revealed.Clear();
        _noJump.Clear();
        _windowEnd = DateTime.MinValue;
        _firedThisRound.Clear();
        return HookResult.Continue;
    }

    // ---------- push detection ----------

    private void CheckPushes()
    {
        if (!Cfg.Enabled || _zones.Count == 0 || DateTime.UtcNow > _windowEnd)
        {
            return;
        }

        foreach (var steamId in Cfg.Targets)
        {
            var player = FindOnline(steamId);
            if (player is null || !player.PawnIsAlive)
            {
                continue;
            }
            if (Cfg.AlertOnlyWhenT && player.Team != CsTeam.Terrorist)
            {
                continue;
            }

            var origin = player.PlayerPawn.Value?.AbsOrigin;
            if (origin is null)
            {
                continue;
            }

            foreach (var zone in _zones)
            {
                if (_firedThisRound.Contains(zone.Name))
                {
                    continue;
                }
                if (Distance(origin, zone) <= zone.Radius)
                {
                    _firedThisRound.Add(zone.Name);
                    Announce(NameOf(player), zone.Name);
                    break; // one alert per poll for this player
                }
            }
        }
    }

    private void Announce(string who, string zone)
    {
        var chat = $"{Prefix} {ChatColors.Red}⚠ {ChatColors.Gold}{who}{ChatColors.Default} is pushing {ChatColors.Yellow}{zone}{ChatColors.Default}!";
        var center = $"<font color='#ff5566'>⚠ {who} — {zone}</font>";
        foreach (var recipient in Recipients())
        {
            recipient.PrintToChat(chat);
            recipient.PrintToCenterHtml(center);
        }
        Logger.LogInfo("Garden/Spotlight", $"Push alert: {who} @ {zone}");
    }

    private IEnumerable<CCSPlayerController> Recipients()
    {
        var audience = Cfg.AlertAudience.Trim().ToLowerInvariant();
        return Utilities.GetPlayers().Where(p =>
            PlayerHelper.IsValid(p) && !p.IsBot && audience switch
            {
                "all" => true,
                "t" => p.Team == CsTeam.Terrorist,
                _ => p.Team == CsTeam.CounterTerrorist,
            });
    }

    // ---------- no-jump (per tick) ----------

    private void OnTick()
    {
        if (_noJump.Count == 0)
        {
            return;
        }

        foreach (var steamId in _noJump)
        {
            var player = FindOnline(steamId);
            var pawn = player?.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid || !player!.PawnIsAlive || pawn.AbsVelocity is null)
            {
                continue;
            }
            // Kill any upward impulse — a jump never gets off the ground.
            if (pawn.AbsVelocity.Z > 0)
            {
                pawn.AbsVelocity.Z = 0;
            }
        }
    }

    // ---------- gag effects ----------

    /// <summary>
    /// Make a player glow through walls for EVERYONE. Setting glow on the player's
    /// own pawn does not network to other clients in CS2, so we spawn a glowing
    /// model clone that follows them (the standard two-prop FollowEntity trick):
    /// a hidden "relay" prop follows the pawn, and a glow prop follows the relay.
    /// </summary>
    private void ApplyGlow(CCSPlayerController? player, ulong steamId)
    {
        RemoveGlow(steamId); // never stack duplicates

        var pawn = player?.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid)
        {
            return;
        }

        try
        {
            var modelName = FallbackModel;
            try
            {
                var candidate = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
                if (!string.IsNullOrEmpty(candidate))
                {
                    modelName = candidate;
                }
            }
            catch { /* keep fallback model */ }

            var relay = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
            var glow = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
            if (relay is null || !relay.IsValid || glow is null || !glow.IsValid)
            {
                return;
            }

            // Relay: invisible follower parented to the pawn (a stable parent for the glow).
            relay.SetModel(modelName);
            relay.Spawnflags = 256u;
            relay.DispatchSpawn();
            relay.Render = Color.FromArgb(0, 255, 255, 255);
            Utilities.SetStateChanged(relay, "CBaseModelEntity", "m_clrRender");

            // Glow prop: the visible-through-walls outline, parented to the relay.
            glow.SetModel(modelName);
            glow.Spawnflags = 256u;
            glow.DispatchSpawn();
            glow.Glow.GlowColorOverride = RevealColor;
            glow.Glow.GlowRange = 5000;
            glow.Glow.GlowRangeMin = 0;
            glow.Glow.GlowTeam = -1;   // everyone sees it
            glow.Glow.GlowType = 3;    // through walls
            Utilities.SetStateChanged(glow, "CBaseModelEntity", "m_Glow");

            relay.AcceptInput("FollowEntity", pawn, relay, "!activator");
            glow.AcceptInput("FollowEntity", relay, glow, "!activator");

            _glowEntities[steamId] = [relay.Index, glow.Index];
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Garden/Spotlight", $"Reveal glow failed: {ex.Message}");
        }
    }

    /// <summary>Remove a player's glow follower entities.</summary>
    private void RemoveGlow(ulong steamId)
    {
        if (!_glowEntities.Remove(steamId, out var indexes))
        {
            return;
        }
        foreach (var index in indexes)
        {
            var entity = Utilities.GetEntityFromIndex<CBaseEntity>((int) index);
            if (entity is not null && entity.IsValid)
            {
                entity.Remove();
            }
        }
    }

    // ---------- commands ----------

    private void OnRevealCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!_admin.Require(player, info, AdminLevel.Moderator))
        {
            return;
        }
        if (!ResolveTarget(info.GetArg(1), out var target) || target is null)
        {
            info.ReplyToCommand($"{Prefix} No such player.");
            return;
        }

        var seconds = double.TryParse(info.GetArg(2), out var s) && s > 0 ? s : Cfg.RevealDefaultSeconds;
        var steamId = target.SteamID;
        _revealed.Add(steamId);
        ApplyGlow(target, steamId);
        Server.PrintToChatAll($"{Prefix} 🔦 {NameOf(target)} is lit up for everyone ({seconds:0}s).");

        _plugin.AddTimer((float) seconds, () =>
        {
            if (_revealed.Remove(steamId))
            {
                RemoveGlow(steamId);
            }
        });
    }

    private void OnNoJumpCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!_admin.Require(player, info, AdminLevel.Moderator))
        {
            return;
        }
        if (!ResolveTarget(info.GetArg(1), out var target) || target is null)
        {
            info.ReplyToCommand($"{Prefix} No such player.");
            return;
        }

        if (_noJump.Remove(target.SteamID))
        {
            Server.PrintToChatAll($"{Prefix} 🦵 {NameOf(target)} can jump again.");
        }
        else
        {
            _noJump.Add(target.SteamID);
            Server.PrintToChatAll($"{Prefix} 🚫 {NameOf(target)} can't jump this round!");
        }
    }

    private void OnStatusCommand(CCSPlayerController? player, CommandInfo info)
    {
        var targets = Cfg.Targets.Count == 0 ? "(none)" : string.Join(", ", Cfg.Targets);
        info.ReplyToCommand($"{Prefix} Spotlight: {(Cfg.Enabled ? "on" : "off")} · alias {Cfg.Alias} · " +
            $"window {Cfg.AlertWindowSeconds:0}s → {Cfg.AlertAudience.ToUpperInvariant()} · zones {_zones.Count} · " +
            $"auto[reveal:{(Cfg.AutoReveal ? "y" : "n")} nojump:{(Cfg.AutoNoJump ? "y" : "n")}]");
        info.ReplyToCommand($"{Prefix} Targets: {targets}");
    }

    private void OnZoneCommand(CCSPlayerController? player, CommandInfo info)
    {
        var action = info.GetArg(1).ToLowerInvariant();

        if (action is "" or "list")
        {
            info.ReplyToCommand($"{Prefix} Zones on {Server.MapName}: {_zones.Count}");
            foreach (var z in _zones)
            {
                info.ReplyToCommand($"{Prefix}  {z.Name} — r{z.Radius:0} @ ({z.X:0}, {z.Y:0}, {z.Z:0})");
            }
            return;
        }

        if (!_admin.Require(player, info, AdminLevel.Moderator))
        {
            return;
        }

        switch (action)
        {
            case "add":
            {
                var pawn = player?.PlayerPawn.Value;
                var origin = pawn?.AbsOrigin;
                if (player is null || origin is null)
                {
                    info.ReplyToCommand($"{Prefix} Stand in the map and run this in-game.");
                    return;
                }
                var name = info.GetArg(2);
                if (string.IsNullOrWhiteSpace(name))
                {
                    info.ReplyToCommand($"{Prefix} Usage: !pushzone add <name> [radius]");
                    return;
                }
                var radius = float.TryParse(info.GetArg(3), out var r) && r > 0 ? r : 300f;
                _zones.RemoveAll(z => z.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                _zones.Add(new SpotlightZone { Name = name, X = origin.X, Y = origin.Y, Z = origin.Z, Radius = radius });
                SaveZones();
                info.ReplyToCommand($"{Prefix} Zone '{name}' saved (r{radius:0}) at your feet.");
                return;
            }
            case "del":
            case "remove":
            {
                var name = info.GetArg(2);
                var removed = _zones.RemoveAll(z => z.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                SaveZones();
                info.ReplyToCommand($"{Prefix} {(removed > 0 ? $"Removed '{name}'." : "No such zone.")}");
                return;
            }
            case "clear":
                _zones.Clear();
                SaveZones();
                info.ReplyToCommand($"{Prefix} All zones cleared on {Server.MapName}.");
                return;
            default:
                info.ReplyToCommand($"{Prefix} Usage: !pushzone add <name> [radius] | del <name> | list | clear");
                return;
        }
    }

    // ---------- helpers ----------

    private static double Distance(Vector a, SpotlightZone z)
    {
        double dx = a.X - z.X, dy = a.Y - z.Y, dz = a.Z - z.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private string NameOf(CCSPlayerController player)
    {
        // Prefer the configured alias for the primary target; else the in-game name.
        if (Cfg.Targets.Count > 0 && player.SteamID == Cfg.Targets[0] && !string.IsNullOrWhiteSpace(Cfg.Alias))
        {
            return Cfg.Alias;
        }
        return player.PlayerName;
    }

    private static CCSPlayerController? FindOnline(ulong steamId) =>
        Utilities.GetPlayers().FirstOrDefault(p => PlayerHelper.IsValid(p) && p.SteamID == steamId);

    /// <summary>Empty arg → the primary configured target; else SteamID64 or partial name.</summary>
    private bool ResolveTarget(string arg, out CCSPlayerController? target)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            target = Cfg.Targets.Count > 0 ? FindOnline(Cfg.Targets[0]) : null;
            return target is not null;
        }
        if (ulong.TryParse(arg, out var steamId) && steamId > 76500000000000000)
        {
            target = FindOnline(steamId);
            return target is not null;
        }
        target = Utilities.GetPlayers().FirstOrDefault(p =>
            PlayerHelper.IsValid(p) && !p.IsBot &&
            p.PlayerName.Contains(arg, StringComparison.OrdinalIgnoreCase));
        return target is not null;
    }

    private void LoadZones(string mapName)
    {
        _zones.Clear();
        try
        {
            var dir = Path.Combine(_plugin.ModuleDirectory, "spotlight_zones");
            _zonesPath = Path.Combine(dir, $"{mapName}.json");
            if (File.Exists(_zonesPath))
            {
                var loaded = JsonSerializer.Deserialize<List<SpotlightZone>>(File.ReadAllText(_zonesPath));
                if (loaded is not null)
                {
                    _zones.AddRange(loaded);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Garden/Spotlight", $"Zone load failed: {ex.Message}");
        }
    }

    private void SaveZones()
    {
        try
        {
            var dir = Path.GetDirectoryName(_zonesPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(_zonesPath,
                JsonSerializer.Serialize(_zones, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.LogError("Garden/Spotlight", $"Zone save failed: {ex.Message}");
        }
    }
}

/// <summary>A named spherical push zone (world-space center + radius, units).</summary>
public class SpotlightZone
{
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; } = 300f;
}
