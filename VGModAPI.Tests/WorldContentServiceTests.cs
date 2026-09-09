using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldContentServiceTests
{
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
        Assert.Equal(WorldStatus.Unavailable, provider.CreatePersistentCombatSite(Guid.NewGuid(), "PoiX", Guid.NewGuid(), "system", 0, 0).Status);
        service.Dispose();
        Assert.Equal(WorldStatus.UnknownProvider, provider.Register(definition));
        Assert.Null(service.AcquireProvider(plugin)); provider.Dispose(); provider.Dispose();
        Assert.Equal(1, released);
    }
}
