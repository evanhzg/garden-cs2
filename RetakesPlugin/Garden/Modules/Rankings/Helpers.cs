using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using GardenRankingsCore;

namespace GardenRankings;

public static class Helpers
{
    public static bool PlayerIsValid(CCSPlayerController? player)
    {
        return player is not null && player.IsValid;
    }

    public static bool IsHumanPlayer(CCSPlayerController? player)
    {
        return PlayerIsValid(player) && !player!.IsBot && !player.IsHLTV;
    }

    public static ulong GetSteamId(CCSPlayerController? player)
    {
        if (!PlayerIsValid(player))
        {
            return 0;
        }

        return player?.AuthorizedSteamID?.SteamId64 ?? 0;
    }

    public static void WriteNewlineDelimited(string message, Action<string> writer)
    {
        foreach (var line in message.Split("\n"))
        {
            writer($"{PluginInfo.MessagePrefix}{line}");
        }
    }

    public static void PrintToAll(string message)
    {
        WriteNewlineDelimited(message, Server.PrintToChatAll);
    }

    private static CCSGameRules? GetGameRules()
    {
        try
        {
            var gameRulesEntities = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules");
            return gameRulesEntities.First().GameRules;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsWarmup()
    {
        return GetGameRules()?.WarmupPeriod ?? false;
    }

    /// <summary>
    /// Human players currently on T or CT.
    /// </summary>
    public static List<CCSPlayerController> GetTeamHumanPlayers()
    {
        return Utilities.GetPlayers()
            .Where(p => IsHumanPlayer(p) && p.Connected == PlayerConnectedState.Connected)
            .Where(p => p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            .ToList();
    }

    public static int CountTeamHumanPlayers()
    {
        return GetTeamHumanPlayers().Count;
    }
}
