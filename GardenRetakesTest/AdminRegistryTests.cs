using GardenRetakes.Core.Admin;
using NUnit.Framework;

namespace GardenRetakes.Test;

[TestFixture]
public class AdminRegistryTests
{
    private const ulong Owner = 76561198000000001;
    private const ulong Target = 76561198000000002;
    private const ulong Third = 76561198000000003;

    private static AdminRegistry NewRegistryWithOwner()
    {
        var registry = new AdminRegistry();
        registry.BootstrapConfigOwners([Owner]);
        return registry;
    }

    [Test]
    public void ConfigOwnersAreOwners()
    {
        var registry = NewRegistryWithOwner();
        Assert.That(registry.GetLevel(Owner), Is.EqualTo(AdminLevel.Owner));
        Assert.That(registry.HasLevel(Owner, AdminLevel.Moderator), Is.True);
    }

    [Test]
    public void OwnerCanAddAndRemoveAdmins()
    {
        var registry = NewRegistryWithOwner();

        Assert.That(registry.TryAdd(Owner, AdminLevel.Owner, Target, "Bob", AdminLevel.Admin, out var error), Is.True);
        Assert.That(error, Is.Null);
        Assert.That(registry.GetLevel(Target), Is.EqualTo(AdminLevel.Admin));

        Assert.That(registry.TryRemove(AdminLevel.Owner, Target, out error), Is.True);
        Assert.That(registry.GetLevel(Target), Is.EqualTo(AdminLevel.None));
    }

    [Test]
    public void NonOwnersCannotManageAdmins()
    {
        var registry = NewRegistryWithOwner();
        registry.TryAdd(Owner, AdminLevel.Owner, Target, "Bob", AdminLevel.Admin, out _);

        Assert.That(registry.TryAdd(Target, AdminLevel.Admin, Third, "Eve", AdminLevel.Moderator, out var error), Is.False);
        Assert.That(error, Is.EqualTo("not_owner"));

        Assert.That(registry.TryRemove(AdminLevel.Admin, Owner, out error), Is.False);
        Assert.That(error, Is.EqualTo("not_owner"));
    }

    [Test]
    public void ConfigOwnersCannotBeRemoved()
    {
        var registry = new AdminRegistry();
        registry.BootstrapConfigOwners([Owner, Target]);

        Assert.That(registry.TryRemove(AdminLevel.Owner, Target, out var error), Is.False);
        Assert.That(error, Is.EqualTo("config_owner"));
    }

    [Test]
    public void RemovingUnknownAdminFails()
    {
        var registry = NewRegistryWithOwner();
        Assert.That(registry.TryRemove(AdminLevel.Owner, Third, out var error), Is.False);
        Assert.That(error, Is.EqualTo("not_found"));
    }

    [Test]
    public void SerializationRoundTripsWithoutConfigOwners()
    {
        var registry = NewRegistryWithOwner();
        registry.TryAdd(Owner, AdminLevel.Owner, Target, "Bob", AdminLevel.Moderator, out _);

        var json = registry.Serialize();
        // The owner must not be persisted as an ENTRY (their id may still appear
        // as Bob's "addedBy" attribution, which is fine).
        Assert.That(json, Does.Not.Contain($"\"steamId\": {Owner}"), "config owners must not be persisted");

        var restored = new AdminRegistry();
        restored.BootstrapConfigOwners([Owner]);
        restored.Load(json);

        Assert.That(restored.GetLevel(Target), Is.EqualTo(AdminLevel.Moderator));
        Assert.That(restored.GetLevel(Owner), Is.EqualTo(AdminLevel.Owner));
    }
}
