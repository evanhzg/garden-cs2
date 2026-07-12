using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;

namespace RetakesAllocatorTest;

[SetUpFixture]
public class GlobalSetup
{
    [OneTimeSetUp]
    public void Setup()
    {
        Configs.Load(".", true);
        Queries.Migrate();
        // Garden: CSS 1.0.367's JsonStringLocalizer NREs outside a real plugin
        // environment (and the lang folder moved to RetakesPlugin/lang in the
        // merged repo), so tests use a tiny self-contained localizer instead.
        Translator.Initialize(new TestJsonLocalizer("../../../../RetakesPlugin/lang/en.json"));
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        Queries.Disconnect();
    }
}

/// <summary>
/// Minimal IStringLocalizer over a single lang JSON file — enough for the
/// Translator to produce the real English strings the tests assert on.
/// </summary>
public class TestJsonLocalizer : IStringLocalizer
{
    private readonly Dictionary<string, string> _strings;

    public TestJsonLocalizer(string jsonPath)
    {
        _strings = File.Exists(jsonPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(jsonPath)) ?? new()
            : new();
    }

    public LocalizedString this[string name]
    {
        get
        {
            var found = _strings.TryGetValue(name, out var value);
            return new LocalizedString(name, found ? value! : name, resourceNotFound: !found);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            if (!_strings.TryGetValue(name, out var value))
            {
                return new LocalizedString(name, name, resourceNotFound: true);
            }

            // No args -> return the raw template (Format would throw on stray {0}).
            var formatted = arguments.Length == 0
                ? value
                : string.Format(CultureInfo.InvariantCulture, value, arguments);
            return new LocalizedString(name, formatted, resourceNotFound: false);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        _strings.Select(kv => new LocalizedString(kv.Key, kv.Value, false));
}

public abstract class BaseTestFixture
{
    [SetUp]
    public void GlobalSetup()
    {
        Configs.Load(".");
        Queries.Wipe();
    }
}
