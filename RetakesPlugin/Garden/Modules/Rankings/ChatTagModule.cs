using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using GardenRankingsCore.Db;
using GardenRankingsCore.Managers;
using RetakesPluginShared.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RetakesPlugin.Garden.Modules.Rankings;

public class ChatTagModule : IGardenModule
{
    public string Name => "ChatTags";
    public bool Enabled => true;

    private readonly RetakesPlugin _plugin;
    private readonly ConcurrentDictionary<ulong, string> _chatTags = new();

    public ChatTagModule(RetakesPlugin plugin)
    {
        _plugin = plugin;
    }

    public void Load(bool hotReload)
    {
        _plugin.AddCommandListener("say", OnPlayerChat, HookMode.Pre);
        _plugin.AddCommandListener("say_team", OnPlayerTeamChat, HookMode.Pre);
        
        _plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        _plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect);
        
        RefreshTags();
    }

    public void Unload()
    {
        _plugin.RemoveCommandListener("say", OnPlayerChat, HookMode.Pre);
        _plugin.RemoveCommandListener("say_team", OnPlayerTeamChat, HookMode.Pre);
    }

    public void OnMapStart(string mapName)
    {
        RefreshTags();
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        RefreshTags();
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        RefreshTags();
        return HookResult.Continue;
    }

    private void RefreshTags()
    {
        Task.Run(() =>
        {
            try
            {
                var seasonId = SeasonManager.Instance.ActiveSeasonId;
                var topPlayers = Queries.GetTopPlayers(seasonId, 3).Select(p => p.SteamId).ToList();
                
                var newTags = new Dictionary<ulong, string>();
                var connected = Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot).Select(p => p.SteamID).ToList();
                
                foreach (var steamId in connected)
                {
                    if (steamId == 0) continue;
                    var placement = Queries.GetPlacement(seasonId, steamId);
                    var elo = placement?.Elo ?? 1000;
                    
                    var tag = "[SILVER][SILVER][NORMAL]";
                    if (topPlayers.Count > 0 && steamId == topPlayers[0]) tag = "[RED][#1 GLOBAL][NORMAL]";
                    else if (topPlayers.Count > 1 && steamId == topPlayers[1]) tag = "[LIGHT_RED][#2 GLOBAL][NORMAL]";
                    else if (topPlayers.Count > 2 && steamId == topPlayers[2]) tag = "[ORANGE][#3 GLOBAL][NORMAL]";
                    else if (elo >= 1600) tag = "[PURPLE][DIAMOND][NORMAL]";
                    else if (elo >= 1300) tag = "[BLUE][PLATINUM][NORMAL]";
                    else if (elo >= 1000) tag = "[GOLD][GOLD][NORMAL]";
                    
                    newTags[steamId] = tag;
                }
                
                foreach (var kvp in newTags)
                {
                    _chatTags[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception)
            {
                // Ignore DB transient errors
            }
        });
    }

    private HookResult OnPlayerChat(CCSPlayerController? player, CommandInfo commandInfo)
    {
        return HandleChat(player, commandInfo, teamOnly: false);
    }

    private HookResult OnPlayerTeamChat(CCSPlayerController? player, CommandInfo commandInfo)
    {
        return HandleChat(player, commandInfo, teamOnly: true);
    }

    private HookResult HandleChat(CCSPlayerController? player, CommandInfo commandInfo, bool teamOnly)
    {
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        var message = commandInfo.GetArg(1);
        if (string.IsNullOrWhiteSpace(message)) return HookResult.Continue;

        if (message.StartsWith('!') || message.StartsWith('/') || message.StartsWith('.'))
        {
            return HookResult.Continue;
        }

        var steamId = player.SteamID;
        var tag = _chatTags.TryGetValue(steamId, out var t) ? t : "[SILVER][SILVER][NORMAL]";
        
        var isAlive = player.PawnIsAlive ? "" : "*DEAD* ";
        if (player.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.Spectator) isAlive = "*SPEC* ";
        
        var translatedTag = GardenRankingsCore.Translator.Color(tag);
        var translatedDead = GardenRankingsCore.Translator.Color(isAlive);

        if (teamOnly)
        {
            var teamStr = player.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist ? "T" : "CT";
            var teamPrefix = GardenRankingsCore.Translator.Color($"[BLUE](Team {teamStr})[NORMAL] ");
            var formatted = $" {translatedDead}{teamPrefix}{translatedTag} {player.PlayerName}: {message}";
            
            foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && p.Team == player.Team))
            {
                p.PrintToChat(formatted);
            }
        }
        else
        {
            var formatted = $" {translatedDead}{translatedTag} {player.PlayerName}: {message}";
            Server.PrintToChatAll(formatted);
        }

        return HookResult.Handled;
    }
}
