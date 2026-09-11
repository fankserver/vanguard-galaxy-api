using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldContentServiceTests
{
    [Fact]
    public void TypedAvailabilityTracksShutdownWithoutReplacingTheService()
    {
        var hub = new LifecycleHub((_, error) => throw error);
        hub.SetCapability("world-authoring", true, "Test bindings.");
        using var definitions = new WorldDefinitionRegistry((_, _) => null, hub.CheckThread);
        using var service = new WorldContentService(hub, definitions, null!, () => false);
        IWorldService retained = service;
        Assert.True(retained.Availability.IsAvailable);
        int changes = 0;
        retained.AvailabilityChanged += _ => changes++;
        service.Dispose();
        Assert.False(retained.Availability.IsAvailable);
        Assert.Equal(ServiceUnavailableReason.ApiStopped, retained.Availability.Reason);
        Assert.Equal(1, changes);
        Assert.Null(retained.AcquireProvider(new object()));
    }

    [Fact]
    public void SequentialAuthenticationDoesNotDependOnReadinessAndContextLossClosesOperations()
    {
        var hub = new LifecycleHub((_, error) => throw error);
        var a = new object(); var b = new object(); int authenticated = 0; bool current = true;
        using var definitions = new WorldDefinitionRegistry((instance, caller) =>
        {
            if (!current || (instance != a && instance != b)) return null;
            authenticated++;
            return new StoryHostPlugin(instance == a ? "author.a" : "author.b", caller);
        }, hub.CheckThread);
        using var service = new WorldContentService(hub, definitions, null!, () => current && authenticated == 2);
        using var first = service.AcquireProvider(a); Assert.NotNull(first);
        var definition = new CombatSiteDefinition("PoiX", 1, "Site", "player", 1);
        Assert.Equal(WorldStatus.Succeeded, first!.RegisterCombatSite(definition));
        Assert.Equal(WorldStatus.Unavailable, ((VGModAPI.Core.IWorldProviderEngine)first).CreatePersistentCombatSite(Guid.NewGuid(), "PoiX", Guid.NewGuid(), "system", 0, 0).Status);
        using var second = service.AcquireProvider(b); Assert.NotNull(second);
        Assert.Equal(WorldStatus.Succeeded, second!.RegisterCombatSite(definition));
        Assert.Equal(WorldStatus.NotReady, ((VGModAPI.Core.IWorldProviderEngine)first).CreatePersistentCombatSite(Guid.NewGuid(), "PoiX", Guid.NewGuid(), "system", 0, 0).Status);
        current = false;
        Assert.Equal(WorldStatus.Unavailable, ((VGModAPI.Core.IWorldProviderEngine)first).CreatePersistentCombatSite(Guid.NewGuid(), "PoiX", Guid.NewGuid(), "system", 0, 0).Status);
        Assert.Equal(WorldStatus.Unavailable, ((VGModAPI.Core.IWorldProviderEngine)second).FindPersistentCombatSite(Guid.NewGuid(), new CombatSiteReference("author.b", "PoiX", Guid.NewGuid())).Status);
    }

    [Fact]
    public void PublicFacadeAuthenticatesCallerAndKeepsClosedNativeGateClosed()
    {
        var hub = new LifecycleHub((_, error) => throw error);
        var plugin = new object();
        using var definitions = new WorldDefinitionRegistry((instance, caller) =>
        {
            Assert.Same(plugin, instance); Assert.Same(typeof(WorldContentServiceTests).Assembly, caller);
            return new StoryHostPlugin("author.a", caller);
        }, hub.CheckThread);
        int released = 0;
        using var service = new WorldContentService(hub, definitions, null!, () => false, () => released++);
        var provider = service.AcquireProvider(plugin); Assert.NotNull(provider);
        var definition = new CombatSiteDefinition("PoiX", 1, "Unicode 星", "player", 1);
        Assert.Equal(WorldStatus.Succeeded, provider!.RegisterCombatSite(definition));
        Assert.Equal(WorldStatus.DuplicateDefinition, provider.RegisterCombatSite(definition));
        Assert.Equal(WorldStatus.InvalidDefinition, provider.RegisterCombatSite(new CombatSiteDefinition("Bad", 0, "Site", "player", 1)));
        Assert.Null(service.AcquireProvider(plugin));
        Assert.Equal(WorldStatus.Succeeded, provider.RegisterCombatSite(new CombatSiteDefinition("Migrate", 2, "New", "player", 1), new CombatSiteDefinition("Migrate", 1, "Old", "player", 1)));
        var prior = new WorldSavedDefinition("author.a", new WorldCombatDefinition("Migrate", 1, "Old", "player", 1));
        Assert.True(definitions.MatchesRetained(prior)); Assert.Equal("New", definitions.Effective(prior)!.Definition.Name);
        Assert.False(definitions.MatchesRetained(new WorldSavedDefinition("author.a", new WorldCombatDefinition("Migrate", 1, "Different", "player", 1))));
        Assert.Equal(WorldStatus.InvalidDefinition, provider.RegisterCombatSite(new CombatSiteDefinition("BadMigration", 2, "New", "player", 2), new CombatSiteDefinition("BadMigration", 1, "Old", "player", 1)));
        Assert.Equal(WorldStatus.Unavailable, ((VGModAPI.Core.IWorldProviderEngine)provider).CreatePersistentCombatSite(Guid.NewGuid(), "PoiX", Guid.NewGuid(), "system", 0, 0).Status);
        service.Dispose();
        Assert.Equal(WorldStatus.UnknownProvider, provider.RegisterCombatSite(definition));
        Assert.Null(service.AcquireProvider(plugin)); provider.Dispose(); provider.Dispose();
        Assert.Equal(1, released);
    }
}
