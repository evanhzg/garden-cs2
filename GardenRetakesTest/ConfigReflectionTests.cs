using GardenRetakes.Core.Config;
using NUnit.Framework;

namespace GardenRetakes.Test;

[TestFixture]
public class ConfigReflectionTests
{
    private enum SampleMode
    {
        Auto,
        Manual,
    }

    private class SampleSub
    {
        public int MinPlayers { get; set; } = 4;
        public bool AutoActivate { get; set; } = true;
        public double Factor { get; set; } = 0.62;
        public SampleMode Mode { get; set; } = SampleMode.Auto;
    }

    private class SampleRoot
    {
        public string Name { get; set; } = "garden";
        public SampleSub Ranked { get; set; } = new();
        public List<string> Maps { get; set; } = ["de_mirage", "de_inferno"];
    }

    [Test]
    public void ReadsLeafValues()
    {
        var root = new SampleRoot();
        Assert.That(ConfigReflection.TryDescribe(root, "Ranked.MinPlayers", out var lines, out var error), Is.True);
        Assert.That(error, Is.Null);
        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Is.EqualTo("MinPlayers = 4"));
    }

    [Test]
    public void ListsSectionsAndCollections()
    {
        var root = new SampleRoot();
        Assert.That(ConfigReflection.TryDescribe(root, null, out var lines, out _), Is.True);
        Assert.That(lines, Has.Some.EqualTo("Name = \"garden\""));
        Assert.That(lines, Has.Some.EqualTo("Ranked {...}"));
        Assert.That(lines, Has.Some.Contains("Maps = [list: 2 items]"));
    }

    [TestCase("Ranked.MinPlayers", "6", "6")]
    [TestCase("ranked.minplayers", "6", "6")] // case-insensitive
    [TestCase("Ranked.AutoActivate", "off", "false")]
    [TestCase("Ranked.Factor", "0.75", "0.75")]
    [TestCase("Ranked.Mode", "manual", "Manual")]
    [TestCase("Name", "roses", "\"roses\"")]
    public void SetsScalarValues(string path, string raw, string expectedDisplay)
    {
        var root = new SampleRoot();
        Assert.That(ConfigReflection.TrySet(root, path, raw, out var oldValue, out var error), Is.True);
        Assert.That(error, Is.Null);
        Assert.That(oldValue, Is.Not.Null);

        ConfigReflection.TryDescribe(root, path, out var lines, out _);
        Assert.That(lines[0], Does.EndWith($"= {expectedDisplay}"));
    }

    [Test]
    public void RejectsBadValues()
    {
        var root = new SampleRoot();
        Assert.That(ConfigReflection.TrySet(root, "Ranked.MinPlayers", "lots", out _, out var error), Is.False);
        Assert.That(error, Does.StartWith("bad_value"));
        Assert.That(root.Ranked.MinPlayers, Is.EqualTo(4), "value must be unchanged after a failed set");
    }

    [Test]
    public void RejectsUnknownPathsAndCollections()
    {
        var root = new SampleRoot();
        Assert.That(ConfigReflection.TrySet(root, "Ranked.Nope", "1", out _, out var error), Is.False);
        Assert.That(error, Does.StartWith("unknown:"));

        Assert.That(ConfigReflection.TrySet(root, "Maps", "de_dust2", out _, out error), Is.False);
        Assert.That(error, Does.StartWith("not_settable"));

        Assert.That(ConfigReflection.TrySet(root, "Name.Sub", "x", out _, out error), Is.False);
        Assert.That(error, Does.StartWith("not_a_section"));
    }
}
