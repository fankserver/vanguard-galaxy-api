using System;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarContentServiceTests
{
    private sealed class Storage : IPersistenceApi, IPersistenceRegistration, IPersistenceReadiness
    {
        internal PersistenceProvider Provider = null!;
        public bool MutationAllowed { get; set; } = true;
        public bool StateReady { get; set; } = true;
        public string Status => "test";
        public IPersistenceRegistration Register(PersistenceProvider provider) { Provider = provider; return this; }
        public void Dispose() { MutationAllowed = false; StateReady = false; }
    }
    private static BarPatronDefinition Definition(BarPatronRetention retention = BarPatronRetention.Persistent) =>
        new("contact", "station", "Name", "Description", "seed", retention);
    private static Guid Ready(LifecycleHub hub, Storage storage, byte[]? bytes = null)
    {
        var session = hub.Begin(SessionOrigin.SaveLoad, "slot");
        hub.PlayerReady(session); storage.Provider.Restore(hub.CurrentSession!, bytes); hub.GameplayInitialized(session);
        return session;
    }

    [Fact]
    public void AuthenticatedOwnersReuseLocalNamesWithoutCrossOwnerRemoval()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var service = new BarContentService(storage, hub,
            (plugin, _) => plugin is string id ? new StoryHostPlugin(id, typeof(BarContentServiceTests).Assembly) : null,
            _ => false, hub.CheckThread);
        var a = service.AcquireProvider("author.a").Provider!;
        var b = service.AcquireProvider("author.b").Provider!;
        Assert.NotEqual(a.ProviderId, b.ProviderId);
        Assert.Equal(BarStatus.AlreadyAcquired, service.AcquireProvider("author.a").Status);
        Assert.Equal(BarStatus.UnknownPlugin, service.AcquireProvider(new object()).Status);
        Assert.True(a.Register(Definition()).Succeeded);
        Assert.True(b.Register(Definition()).Succeeded);
        Assert.Equal(BarStatus.DuplicateLocalId, a.Register(Definition()).Status);
        var session = Ready(hub, storage);
        Assert.True(a.Place(session, "contact").Succeeded);
        Assert.True(b.Place(session, "contact").Succeeded);
        Assert.Equal(2, BarPatronCodec.Decode(storage.Provider.Capture()).Length);
        Assert.True(a.Remove(session, "contact").Succeeded);
        Assert.Equal(b.ProviderId, Assert.Single(BarPatronCodec.Decode(storage.Provider.Capture())).Id.Provider);
        a.Dispose();
        Assert.Equal(BarStatus.Unavailable, a.Place(session, "contact").Status);
        Assert.NotNull(service.AcquireProvider("author.a").Provider);
    }

    [Fact]
    public void WrongAssemblyCannotAcquireAndExclusivePermissionIsExplicit()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var service = new BarContentService(storage, hub,
            (plugin, _) => new StoryHostPlugin((string)plugin, (string)plugin == "foreign" ? typeof(string).Assembly : typeof(BarContentServiceTests).Assembly),
            id => id == "campaign", hub.CheckThread);
        Assert.Equal(BarStatus.CallerMismatch, service.AcquireProvider("foreign").Status);
        var campaign = service.AcquireProvider("campaign").Provider!;
        var jobs = service.AcquireProvider("jobs").Provider!;
        Assert.True(campaign.ConfigureStation("station", BarRosterOwnership.Exclusive).Succeeded);
        Assert.Equal(BarStatus.PermissionDenied, jobs.ConfigureStation("station", BarRosterOwnership.Exclusive).Status);
        Assert.True(jobs.ConfigureStation("station", BarRosterOwnership.Additive).Succeeded);
    }

    [Fact]
    public void TransientPlacementRespectsReadinessAndCannotReplaceSavedPatron()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var service = new BarContentService(storage, hub,
            (_, _) => new StoryHostPlugin("author", typeof(BarContentServiceTests).Assembly), _ => false, hub.CheckThread);
        var author = service.AcquireProvider("author").Provider!;
        Assert.True(author.Register(Definition()).Succeeded);
        var session = Ready(hub, storage);
        Assert.True(author.Place(session, "contact").Succeeded);
        var saved = storage.Provider.Capture();
        author.Dispose();
        author = service.AcquireProvider("author").Provider!;
        Assert.True(author.Register(Definition(BarPatronRetention.Transient)).Succeeded);
        Assert.Equal(BarStatus.InvalidDefinition, author.Place(session, "contact").Status);
        Assert.Equal(saved, storage.Provider.Capture());
        session = Ready(hub, storage);
        storage.MutationAllowed = false;
        Assert.Equal(BarStatus.Unavailable, author.Place(session, "contact").Status);
        storage.MutationAllowed = true;
        Assert.True(author.Place(session, "contact").Succeeded);
        Assert.Empty(BarPatronCodec.Decode(storage.Provider.Capture()));
        var old = session;
        session = Ready(hub, storage);
        Assert.Equal(BarStatus.StaleSession, author.Remove(old, "contact").Status);
        Assert.Equal(BarStatus.Unavailable, author.Remove(session, "contact").Status);
    }
}
