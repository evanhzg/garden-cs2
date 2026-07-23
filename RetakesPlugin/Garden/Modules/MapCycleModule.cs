using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using GardenRetakes.Core.GameModes;
using RetakesPlugin.Utils;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace RetakesPlugin.Garden.Modules;

public class MapCycleSessionState
{
    public DateTime LastDisconnectTime { get; set; } = DateTime.MinValue;
    public HashSet<string> PlayedMaps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class MapCycleModule : IGardenModule
{
    private readonly RetakesPlugin _plugin;
    private readonly GardenHost _host;
    private readonly string _sessionFilePath;
    private MapCycleSessionState _session = new();
    
    private readonly HashSet<ulong> _rtvPlayers = [];
    private bool _voteInProgress = false;
    private Dictionary<ulong, string> _playerVotes = [];
    private Timer? _voteTimer;

    public string Name => "MapCycle";
    public bool Enabled => _host.Settings.MapCycle.Enabled;

    public MapCycleModule(RetakesPlugin plugin, GardenHost host)
    {
        _plugin = plugin;
        _host = host;
        _sessionFilePath = Path.Combine(plugin.ModuleDirectory, "mapcycle_session.json");
    }

    public void Load(bool hotReload)
    {
        _plugin.AddCommand("css_rtv", "Rock the vote to change the map.", OnRtvCommand);
        _plugin.AddCommandListener("say", OnPlayerSay);
        _plugin.AddCommandListener("say_team", OnPlayerSay);
        
        _plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        _plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Pre);

        LoadSession();
    }

    public void OnMapStart(string mapName)
    {
        _rtvPlayers.Clear();
        _voteInProgress = false;
        _playerVotes.Clear();
        _voteTimer?.Kill();
        _voteTimer = null;

        if (!Enabled) return;

        LoadSession();
        
        _session.PlayedMaps.Add(mapName);
        SaveSession();

        // Random Startup Map: check if current map is in the current mode's pool.
        var currentModeStr = _host.Modes.CurrentMode.ToString();
        var mapGroups = _host.Settings.MapCycle.MapGroups;
        
        if (mapGroups.TryGetValue(currentModeStr, out var maps) && maps.Count > 0)
        {
            if (!IsMapInPool(mapName, maps))
            {
                Logger.LogInfo("Garden/MapCycle", $"Current map {mapName} is not in {currentModeStr} pool. Changing to a random map.");
                ChangeToRandomMap();
            }
        }
    }

    public void Unload()
    {
    }
    
    private void LoadSession()
    {
        try
        {
            if (File.Exists(_sessionFilePath))
            {
                var json = File.ReadAllText(_sessionFilePath);
                _session = JsonSerializer.Deserialize<MapCycleSessionState>(json) ?? new MapCycleSessionState();
            }
            
            // Check if session expired
            if (Utilities.GetPlayers().Count(p => PlayerHelper.IsValid(p)) == 0)
            {
                if ((DateTime.UtcNow - _session.LastDisconnectTime).TotalMinutes > 10)
                {
                    Logger.LogInfo("Garden/MapCycle", "Session expired (server empty for >10 mins). Clearing played maps.");
                    _session.PlayedMaps.Clear();
                    SaveSession();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("Garden/MapCycle", ex);
            _session = new MapCycleSessionState();
        }
    }

    private void SaveSession()
    {
        try
        {
            var json = JsonSerializer.Serialize(_session, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_sessionFilePath, json);
        }
        catch (Exception ex)
        {
            Logger.LogException("Garden/MapCycle", ex);
        }
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        if (!Enabled) return HookResult.Continue;
        
        var count = Utilities.GetPlayers().Count(p => PlayerHelper.IsValid(p));
        if (count == 1) // First player joined
        {
            LoadSession();
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (!Enabled) return HookResult.Continue;
        
        var player = @event.Userid;
        if (player != null && player.IsValid)
        {
            _rtvPlayers.Remove(player.SteamID);
        }

        // If this is the last player disconnecting (count will be 0)
        var count = Utilities.GetPlayers().Count(p => PlayerHelper.IsValid(p) && !p.IsBot);
        if (count <= 1)
        {
            _session.LastDisconnectTime = DateTime.UtcNow;
            SaveSession();
        }
        
        return HookResult.Continue;
    }

    private void OnRtvCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Enabled || player == null || !player.IsValid) return;

        if (_voteInProgress)
        {
            player.PrintToChat($"{_plugin.Localizer["garden.prefix"]} A map vote is already in progress!");
            return;
        }

        if (_rtvPlayers.Add(player.SteamID))
        {
            var validPlayers = Utilities.GetPlayers().Count(p => PlayerHelper.IsValid(p) && !p.IsBot);
            var required = Math.Max(1, (int)Math.Ceiling(validPlayers * _host.Settings.MapCycle.RtvPercentage));
            
            Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} \x04{player.PlayerName}\x01 wants to rock the vote! (\x04{_rtvPlayers.Count}\x01/\x04{required}\x01)");

            if (_rtvPlayers.Count >= required)
            {
                StartMapVote();
            }
        }
        else
        {
            player.PrintToChat($"{_plugin.Localizer["garden.prefix"]} You have already voted to RTV.");
        }
    }

    private HookResult OnPlayerSay(CCSPlayerController? player, CommandInfo info)
    {
        if (!Enabled || !_voteInProgress || player == null || !player.IsValid) return HookResult.Continue;

        var message = info.GetArg(1).Trim();
        if (message.StartsWith('!') || message.StartsWith('/')) return HookResult.Continue;

        var currentModeStr = _host.Modes.CurrentMode.ToString();
        if (!_host.Settings.MapCycle.MapGroups.TryGetValue(currentModeStr, out var maps) || maps.Count == 0)
        {
            return HookResult.Continue;
        }

        var matchedMap = maps.FirstOrDefault(m => m.Contains(message, StringComparison.OrdinalIgnoreCase) || 
                                                 message.Contains(m, StringComparison.OrdinalIgnoreCase));
                                                 
        if (matchedMap == null && message.Length >= 3)
        {
            // Try matching just the map name without prefix
            matchedMap = maps.FirstOrDefault(m => 
            {
                var stripped = m.StartsWith("ws:") ? m.Substring(m.IndexOf(':') + 1) : m;
                stripped = stripped.StartsWith("de_") || stripped.StartsWith("cs_") || stripped.StartsWith("am_") ? stripped.Substring(3) : stripped;
                return stripped.Contains(message, StringComparison.OrdinalIgnoreCase);
            });
        }

        if (matchedMap != null)
        {
            _playerVotes[player.SteamID] = matchedMap;
            player.PrintToChat($"{_plugin.Localizer["garden.prefix"]} You voted for \x04{matchedMap}\x01.");
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    private void StartMapVote()
    {
        if (_voteInProgress) return;
        _voteInProgress = true;
        _playerVotes.Clear();

        var duration = _host.Settings.MapCycle.VoteDurationSeconds;
        Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} \x04RTV successful!\x01 Please type the name of the map you want to play in chat! You have {duration} seconds.");
        
        var currentModeStr = _host.Modes.CurrentMode.ToString();
        if (_host.Settings.MapCycle.MapGroups.TryGetValue(currentModeStr, out var maps) && maps.Count > 0)
        {
            var mapList = string.Join(", ", maps.Select(m => m.StartsWith("ws:") ? m.Substring(m.IndexOf(':') + 1) : m));
            Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} Available maps: \x04{mapList}\x01");
        }

        _voteTimer = _plugin.AddTimer(duration, EndMapVote, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void EndMapVote()
    {
        _voteInProgress = false;
        
        if (_playerVotes.Count == 0)
        {
            Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} No valid votes received. Selecting a random map.");
            ChangeToRandomMap();
            return;
        }

        var voteCounts = _playerVotes.Values.GroupBy(v => v)
                                            .Select(g => new { Map = g.Key, Count = g.Count() })
                                            .OrderByDescending(x => x.Count)
                                            .ToList();

        var maxVotes = voteCounts.First().Count;
        var topMaps = voteCounts.Where(x => x.Count == maxVotes).Select(x => x.Map).ToList();

        var winner = topMaps[Random.Shared.Next(topMaps.Count)];
        
        Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} Voting ended! \x04{winner}\x01 won with {maxVotes} votes.");
        
        _plugin.AddTimer(3.0f, () => ChangeMap(winner), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ChangeToRandomMap()
    {
        var currentModeStr = _host.Modes.CurrentMode.ToString();
        if (!_host.Settings.MapCycle.MapGroups.TryGetValue(currentModeStr, out var maps) || maps.Count == 0)
        {
            Logger.LogWarning("Garden/MapCycle", $"No maps defined for mode {currentModeStr}");
            return;
        }

        var unplayedMaps = maps.Where(m => !_session.PlayedMaps.Contains(m)).ToList();
        
        if (unplayedMaps.Count == 0)
        {
            // All maps played, reset cycle
            Logger.LogInfo("Garden/MapCycle", "All maps played in session. Resetting played cycle.");
            _session.PlayedMaps.Clear();
            SaveSession();
            unplayedMaps = maps.ToList();
        }

        var randomMap = unplayedMaps[Random.Shared.Next(unplayedMaps.Count)];
        Server.PrintToChatAll($"{_plugin.Localizer["garden.prefix"]} Next map is \x04{randomMap}\x01");
        
        _plugin.AddTimer(3.0f, () => ChangeMap(randomMap), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ChangeMap(string mapName)
    {
        if (mapName.StartsWith("ws:", StringComparison.OrdinalIgnoreCase))
        {
            var wsId = mapName.Substring(3);
            Server.ExecuteCommand($"host_workshop_map {wsId}");
        }
        else
        {
            Server.ExecuteCommand($"changelevel {mapName}");
        }
    }

    private bool IsMapInPool(string currentMap, List<string> pool)
    {
        foreach (var map in pool)
        {
            if (map.Equals(currentMap, StringComparison.OrdinalIgnoreCase)) return true;
            if (map.StartsWith("ws:") && map.Substring(3).Equals(currentMap, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
