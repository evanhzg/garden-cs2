using CounterStrikeSharp.API.Modules.Utils;
using GardenRankingsCore.Config;

namespace GardenRankingsCore;

public static class PluginInfo
{
    public const string Version = "1.0.0";

    public static readonly string LogPrefix = $"[GardenRankings {Version}] ";

    public static string MessagePrefix
    {
        get
        {
            var name = "Rankings";
            if (Configs.IsLoaded())
            {
                name = Configs.GetConfigData().ChatMessagePluginName;
            }

            return $"[{ChatColors.Gold}{name}{ChatColors.White}] ";
        }
    }
}
