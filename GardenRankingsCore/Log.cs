using Microsoft.Extensions.Logging;
using GardenRankingsCore.Config;

namespace GardenRankingsCore;

public static class Log
{
    private static LogLevel CurrentLevel =>
        Configs.IsLoaded() ? Configs.GetConfigData().LogLevel : LogLevel.Information;

    private static void Write(LogLevel level, string message)
    {
        if (level < CurrentLevel)
        {
            return;
        }

        var color = level switch
        {
            LogLevel.Trace => ConsoleColor.DarkGray,
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Information => ConsoleColor.Green,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Critical => ConsoleColor.DarkRed,
            _ => ConsoleColor.White,
        };

        Console.ForegroundColor = color;
        Console.WriteLine($"{PluginInfo.LogPrefix}{message}");
        Console.ResetColor();
    }

    public static void Trace(string message) => Write(LogLevel.Trace, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Information, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
}
