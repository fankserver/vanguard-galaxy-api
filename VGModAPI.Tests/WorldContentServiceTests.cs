using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldContentServiceTests
{
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
        var definition = new WorldCombatSiteDefinition("PoiX", 1, "Site", "player", 1);
        Assert.Equal(WorldStatus.Succeeded, first!.Register(definition));
        Assert.Equal(WorldStatus.Unavailable, first.CreatePersistentCombatSite(Guid.NewGuid(), "PoiX", Guid.NewGuid(), "system", 0, 0).Status);
        using var second = service.AcquireProvider(b); Assert.NotNull(second);
        Assert.Equal(WorldStatus.Succeeded, second!.Register(definition));
        Assert.Equal(WorldStatus.NotReady, first.CreatePersistentCombatSite(Guid.NewGuid(), "PoiX", Guid.NewGuid(), "system", 0, 0).Status);
        current = false;
        Assert.Equal(WorldStatus.Unavailable, first.CreatePersistentCombatSite(Guid.NewGuid(), "PoiX", Guid.NewGuid(), "system", 0, 0).Status);
        Assert.Equal(WorldStatus.Unavailable, second.FindPersistentCombatSite(Guid.NewGuid(), new WorldSiteReference("author.b", "PoiX", Guid.NewGuid())).Status);
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
        var definition = new WorldCombatSiteDefinition("PoiX", 1, "Unicode 星", "player", 1);
        Assert.Equal(WorldStatus.Succeeded, provider!.Register(definition));
        Assert.Equal(WorldStatus.DuplicateDefinition, provider.Register(definition));
        Assert.Equal(WorldStatus.InvalidDefinition, provider.Register(new WorldCombatSiteDefinition("Bad", 0, "Site", "player", 1)));
        Assert.Null(service.AcquireProvider(plugin));
        Assert.Equal(WorldStatus.Succeeded, provider.Register(new WorldCombatSiteDefinition("Migrate", 2, "New", "player", 1), new WorldCombatSiteDefinition("Migrate", 1, "Old", "player", 1)));
        var prior = new WorldSavedDefinition("author.a", new WorldCombatDefinition("Migrate", 1, "Old", "player", 1));
        Assert.True(definitions.MatchesRetained(prior)); Assert.Equal("New", definitions.Effective(prior)!.Definition.Name);
        Assert.False(definitions.MatchesRetained(new WorldSavedDefinition("author.a", new WorldCombatDefinition("Migrate", 1, "Different", "player", 1))));
        Assert.Equal(WorldStatus.InvalidDefinition, provider.Register(new WorldCombatSiteDefinition("BadMigration", 2, "New", "player", 2), new WorldCombatSiteDefinition("BadMigration", 1, "Old", "player", 1)));
        Assert.Equal(WorldStatus.Unavailable, provider.CreatePersistentCombatSite(Guid.NewGuid(), "PoiX", Guid.NewGuid(), "system", 0, 0).Status);
        service.Dispose();
        Assert.Equal(WorldStatus.UnknownProvider, provider.Register(definition));
        Assert.Null(service.AcquireProvider(plugin)); provider.Dispose(); provider.Dispose();
        Assert.Equal(1, released);
    }
}
