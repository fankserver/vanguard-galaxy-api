using System;
using System.Reflection;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldDefinitionRegistryTests
{
    private static readonly Assembly Caller = typeof(WorldDefinitionRegistryTests).Assembly;
    private static WorldCombatDefinition Definition() => new("PoiX", 1, "世界-é", "player", 1);
    private static WorldSavedObject Saved(string owner) => new(new WorldObjectIdentity(
        new ContentDeclaration(owner, "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid()), "system-a", new string('a', 64), 1);

    [Fact]
    public void OwnersCanReuseLocalIdsButDuplicatesAndRetiredLeasesAreRejected()
    {
        using var registry = new WorldDefinitionRegistry((plugin, caller) => new StoryHostPlugin((string)plugin, caller), () => { });
        var a = registry.Acquire("author.a", Caller)!; var b = registry.Acquire("author.b", Caller)!;
        Assert.True(a.Register(Definition())); Assert.True(b.Register(Definition()));
        Assert.False(a.Register(Definition())); Assert.Null(registry.Acquire("author.a", Caller));
        Assert.True(registry.HasLiveRevision(Saved("author.a"))); Assert.True(registry.HasLiveRevision(Saved("author.b")));
        long before = registry.Revision; a.Dispose(); Assert.True(registry.Revision > before);
        Assert.False(registry.HasLiveRevision(Saved("author.a"))); Assert.True(registry.HasLiveRevision(Saved("author.b")));
        var replacement = registry.Acquire("author.a", Caller)!;
        Assert.False(a.Register(Definition())); Assert.True(replacement.Register(Definition()));
        a.Dispose(); Assert.True(registry.HasLiveRevision(Saved("author.a")));
    }

    [Fact]
    public void HostIdentityAndCallingAssemblyAreRequired()
    {
        using var absent = new WorldDefinitionRegistry((_, _) => null, () => { });
        Assert.Null(absent.Acquire(new object(), Caller));
        using var wrong = new WorldDefinitionRegistry((_, _) => new StoryHostPlugin("author.a", typeof(string).Assembly), () => { });
        Assert.Null(wrong.Acquire(new object(), Caller));
        WorldDefinitionRegistry? registry = null;
        registry = new WorldDefinitionRegistry((_, caller) => { registry!.Dispose(); return new StoryHostPlugin("author.a", caller); }, () => { });
        Assert.Null(registry.Acquire(new object(), Caller));
    }

    [Fact]
    public void DefinitionDataIsBoundedAndUnicodePreserved()
    {
        Assert.Equal("世界-é", Definition().Name);
        Assert.Throws<ArgumentException>(() => new WorldCombatDefinition("bad/path", 1, "name", "player", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldCombatDefinition("PoiX", 0, "name", "player", 1));
        Assert.Throws<ArgumentException>(() => new WorldCombatDefinition("PoiX", 1, new string('界', 342), "player", 1));
    }
}
