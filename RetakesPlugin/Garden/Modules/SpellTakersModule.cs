using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRetakes.Core.GameModes;
using RetakesPlugin.Utils;
using Fleck;
using System.Text.Json;
using System.Numerics;

namespace RetakesPlugin.Garden.Modules;

public enum SpellClass
{
    Juggernaut, Assassin, Caster, Cleric, Berserker, Ranger, Elementalist, Necromancer, Paladin, Rogue, Sniper, Illusionist
}

public class SpellTakersModule : IGardenModule
{
    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private WebSocketServer? _webSocketServer;
    private readonly List<IWebSocketConnection> _sockets = new();

    private readonly Dictionary<ulong, SpellClass> _playerClasses = new();

    public string Name => "SpellTakers";
    public bool Enabled => true;

    private bool IsActive => _host.Modes.CurrentMode == GameModeKind.SpellTakers;

    public SpellTakersModule(RetakesPlugin plugin, GardenHost host)
    {
        _plugin = plugin;
        _host = host;
    }

    public void Load(bool hotReload)
    {
        _host.Modes.ModeChanged += OnModeChanged;

        _plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        _plugin.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        _plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

        _plugin.AddCommand("css_draft", "Draft your SpellTaker class", OnDraftCommand);

        // Hook Generic TakeDamage to protect the tower
        VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Hook(OnTakeDamage, HookMode.Pre);

        // Start WebSocket Server
        StartWebSocketServer();
    }

    public void Unload()
    {
        _host.Modes.ModeChanged -= OnModeChanged;
        
        _plugin.RemoveCommand("css_draft", OnDraftCommand);
        VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Unhook(OnTakeDamage, HookMode.Pre);

        foreach (var socket in _sockets.ToList())
        {
            socket.Close();
        }
        _sockets.Clear();
        _webSocketServer?.Dispose();
    }

    private void StartWebSocketServer()
    {
        try
        {
            _webSocketServer = new WebSocketServer("ws://0.0.0.0:8080");
            _webSocketServer.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    _sockets.Add(socket);
                    Logger.LogInfo("SpellTakers", $"WebSocket client connected: {socket.ConnectionInfo.ClientIpAddress}");
                };
                socket.OnClose = () =>
                {
                    _sockets.Remove(socket);
                    Logger.LogInfo("SpellTakers", "WebSocket client disconnected.");
                };
                socket.OnMessage = message =>
                {
                    // Handle incoming messages from overlay if needed
                };
            });
            Logger.LogInfo("SpellTakers", "WebSocket server started on ws://0.0.0.0:8080");
        }
        catch (Exception ex)
        {
            Logger.LogException("SpellTakers", ex);
        }
    }

    public void OnMapStart(string mapName)
    {
        _playerClasses.Clear();
    }

    private void OnModeChanged(GameModeKind from, GameModeKind to)
    {
        if (to == GameModeKind.SpellTakers)
        {
            Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} SpellTakers (ARAM) mode started! Type !draft to pick a class.");
        }
    }

    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    private void OnDraftCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !IsActive) return;

        var menu = new ChatMenu("Draft Your Class");
        
        foreach (SpellClass sc in Enum.GetValues(typeof(SpellClass)))
        {
            menu.AddMenuOption(sc.ToString(), (p, option) =>
            {
                if (Enum.TryParse<SpellClass>(option.Text, out var parsedClass))
                {
                    _playerClasses[p.SteamID] = parsedClass;
                    p.PrintToChat($"{_plugin.Localizer["garden.prefix"]} You drafted: {parsedClass}");
                    
                    // Reapply passives immediately if alive
                    if (p.PlayerPawn.IsValid && p.PlayerPawn.Value != null && p.PawnIsAlive)
                    {
                        ApplyClassPassives(p, parsedClass);
                    }
                }
            });
        }

        MenuManager.OpenChatMenu(player, menu);
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (!IsActive) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.PawnIsAlive) return HookResult.Continue;

        if (_playerClasses.TryGetValue(player.SteamID, out var sc))
        {
            ApplyClassPassives(player, sc);
        }

        return HookResult.Continue;
    }

    private void ApplyClassPassives(CCSPlayerController player, SpellClass sc)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;

        // Reset to defaults first
        pawn.GravityScale = 1.0f;
        pawn.VelocityModifier = 1.0f;
        pawn.Health = 100;

        switch (sc)
        {
            case SpellClass.Juggernaut:
            case SpellClass.Paladin:
                pawn.Health = 250;
                pawn.VelocityModifier = 0.8f;
                break;
            case SpellClass.Assassin:
            case SpellClass.Rogue:
                pawn.Health = 90;
                pawn.VelocityModifier = 1.3f;
                pawn.GravityScale = 0.8f; // Higher jumps
                break;
            case SpellClass.Sniper:
            case SpellClass.Ranger:
                pawn.Health = 85;
                pawn.VelocityModifier = 1.1f;
                break;
            case SpellClass.Berserker:
                pawn.Health = 150;
                pawn.VelocityModifier = 1.15f;
                break;
        }

        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    private HookResult OnTakeDamage(DynamicHook hook)
    {
        if (!IsActive) return HookResult.Continue;

        var victim = hook.GetParam<CEntityInstance>(0);
        var damageInfo = hook.GetParam<CTakeDamageInfo>(1);

        if (victim == null || damageInfo == null) return HookResult.Continue;

        // Verify if victim is our tower (assumes targetname starts with "lane_tower")
        // In CounterStrikeSharp, DesignerName is the classname (e.g. "prop_dynamic")
        // Targetname is usually Entity.Name
        if (victim.DesignerName != "prop_dynamic" && victim.DesignerName != "func_breakable") return HookResult.Continue;
        
        string targetName = victim.Entity?.Name ?? "";
        if (!targetName.StartsWith("lane_tower", StringComparison.OrdinalIgnoreCase)) return HookResult.Continue;

        // Get the attacker
        var attackerHandle = damageInfo.Attacker;
        if (attackerHandle == null || !attackerHandle.IsValid) return HookResult.Continue;

        var attackerPawn = attackerHandle.Value as CBasePlayerPawn;
        if (attackerPawn == null || attackerPawn.Controller.Value == null) return HookResult.Continue;

        var controller = attackerPawn.Controller.Value as CCSPlayerController;
        if (controller == null) return HookResult.Continue;

        var steamId = controller.SteamID;

        // Default to Juggernaut if they haven't picked a class
        SpellClass attackerClass = _playerClasses.TryGetValue(steamId, out var sc) ? sc : SpellClass.Juggernaut;

        bool isRanged = IsClassRanged(attackerClass);

        var victimEntity = new CBaseEntity(victim.Handle);
        if (victimEntity == null || victimEntity.CBodyComponent?.SceneNode == null || attackerPawn.CBodyComponent?.SceneNode == null) return HookResult.Continue;

        var aOrigin = attackerPawn.CBodyComponent.SceneNode.AbsOrigin;
        var vOrigin = victimEntity.CBodyComponent.SceneNode.AbsOrigin;

        // Distance Check
        float distance = (float)Math.Sqrt(
            Math.Pow(aOrigin.X - vOrigin.X, 2) +
            Math.Pow(aOrigin.Y - vOrigin.Y, 2) +
            Math.Pow(aOrigin.Z - vOrigin.Z, 2)
        );

        if (!isRanged && distance > 150.0f)
        {
            // Nullify damage
            damageInfo.Damage = 0;
            controller.PrintToCenter("Get closer! Your melee attacks cannot reach the tower.");
            return HookResult.Changed;
        }

        return HookResult.Continue;
    }

    private bool IsClassRanged(SpellClass sc)
    {
        return sc == SpellClass.Caster || sc == SpellClass.Ranger || sc == SpellClass.Elementalist || sc == SpellClass.Sniper || sc == SpellClass.Necromancer || sc == SpellClass.Illusionist || sc == SpellClass.Cleric;
    }

    // Existing event hooks to broadcast to WebSocket
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!IsActive) return HookResult.Continue;

        var victim = @event.Userid;
        var attacker = @event.Attacker;

        if (victim == null || attacker == null) return HookResult.Continue;

        var payload = new
        {
            EventType = "PlayerDeath",
            VictimSteamID = victim.SteamID.ToString(),
            AttackerSteamID = attacker.SteamID.ToString(),
            Weapon = @event.Weapon
        };

        BroadcastEvent(payload);

        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (!IsActive) return HookResult.Continue;

        var victim = @event.Userid;
        var attacker = @event.Attacker;

        if (victim == null) return HookResult.Continue;

        var payload = new
        {
            EventType = "PlayerHurt",
            VictimSteamID = victim.SteamID.ToString(),
            AttackerSteamID = attacker?.SteamID.ToString() ?? "World",
            Damage = @event.DmgHealth,
            RemainingHealth = @event.Health
        };

        BroadcastEvent(payload);

        return HookResult.Continue;
    }

    private void BroadcastEvent(object payload)
    {
        if (_sockets.Count == 0) return;

        try
        {
            var json = JsonSerializer.Serialize(payload);
            foreach (var socket in _sockets.ToList())
            {
                socket.Send(json);
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("SpellTakers", ex);
        }
    }
}
