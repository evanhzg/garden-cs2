// Adapted from the Garden allocator / cs2-retakes Translator
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Localization;

namespace GardenRankingsCore;

public class Translator
{
    private static Translator? _instance;

    public static Translator Initialize(IStringLocalizer localizer)
    {
        _instance = new(localizer);
        return _instance;
    }

    public static bool IsInitialized => _instance is not null;

    public static Translator Instance => _instance ?? throw new Exception("Translator is not initialized.");

    private readonly IStringLocalizer _localizer;

    public Translator(IStringLocalizer localizer)
    {
        _localizer = localizer;
    }

    public string this[string name] => Translate(name);

    public string this[string name, params object[] arguments] => Translate(name, arguments);

    private string Translate(string key, params object[] arguments)
    {
        var isCenter = key.StartsWith("center.");
        key = key.Replace("center.", "");

        var localizedString = _localizer[key, arguments];

        if (localizedString == null || localizedString.ResourceNotFound)
        {
            return key;
        }

        return isCenter ? localizedString.Value : Color(localizedString.Value);
    }

    public static string Color(string text)
    {
        return text
            .Replace("[GREEN]", ChatColors.Green.ToString())
            .Replace("[RED]", ChatColors.Red.ToString())
            .Replace("[YELLOW]", ChatColors.Yellow.ToString())
            .Replace("[BLUE]", ChatColors.Blue.ToString())
            .Replace("[PURPLE]", ChatColors.Purple.ToString())
            .Replace("[ORANGE]", ChatColors.Orange.ToString())
            .Replace("[WHITE]", ChatColors.White.ToString())
            .Replace("[NORMAL]", ChatColors.White.ToString())
            .Replace("[GREY]", ChatColors.Grey.ToString())
            .Replace("[LIGHT_RED]", ChatColors.LightRed.ToString())
            .Replace("[LIGHT_BLUE]", ChatColors.LightBlue.ToString())
            .Replace("[GOLD]", ChatColors.Gold.ToString())
            .Replace("[LIME]", ChatColors.Lime.ToString())
            .Replace("[SILVER]", ChatColors.Silver.ToString());
    }
}
