using System;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarContentServiceTests
{
    [Fact]
    public void InteractionsArePlanBoundNonReentrantAndRevokedWithTheLease()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var service = new BarContentService(storage, hub,
            (_, _) => new StoryHostPlugin("author", typeof(BarContentServiceTests).Assembly), _ => false, hub.CheckThread);
        var author = service.AcquireProvider("author").Provider!;
        BarRosterPlan? plan = null;
        int calls = 0;
        author.Register(Definition(), interaction =>
        {
            calls++;
            Assert.Equal(plan!.Session, interaction.SessionId);
            Assert.Equal(plan.Patrons[0].Id, interaction.PatronId);
            Assert.False(service.Interact(plan, plan.Patrons[0]));
        });
        var session = Ready(hub, storage);
        author.Place(session, "contact");
        plan = service.Plan(session, "station")!;
        Assert.True(service.Interact(plan, plan.Patrons[0]));
        Assert.Equal(1, calls);
        author.Dispose();
        Assert.False(service.Interact(plan, plan.Patrons[0]));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ThrowingInteractionIsIsolatedAndDoesNotPermanentlyLockTheContact()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var service = new BarContentService(storage, hub,
            (_, _) => new StoryHostPlugin("author", typeof(BarContentServiceTests).Assembly), _ => false, hub.CheckThread);
        var author = service.AcquireProvider("author").Provider!;
        int calls = 0;
        author.Register(Definition(), _ => { calls++; throw new InvalidOperationException("provider failure"); });
        var session = Ready(hub, storage);
        author.Place(session, "contact");
        var plan = service.Plan(session, "station")!;
        Assert.False(service.Interact(plan, plan.Patrons[0]));
        Assert.False(service.Interact(plan, plan.Patrons[0]));
        Assert.Equal(2, calls);
    }

    public sealed class ClickPatron { public int seat = 1; }
    public sealed class ClickBar { public System.Collections.Generic.List<ClickPatron> availablePatrons = new(); public long lastUpdateTime = 1; }
    public sealed class ClickStation { public string guid = "station"; public ClickBar bar = new(); }
    public sealed class ClickPlayer { public static ClickPlayer? current; public object? currentPointOfInterest; }

    [Fact]
    public void NativeMembershipIsRecheckedAfterCallbackCapablePlanValidation()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage(); var station = new ClickStation(); var contact = new ClickPatron();
        station.bar.availablePatrons.Add(contact);
        ClickPlayer.current = new ClickPlayer { currentPointOfInterest = station };
        bool invalidate = false;
        using var service = new BarContentService(storage, hub,
            (_, _) => new StoryHostPlugin("author", typeof(BarContentServiceTests).Assembly),
            _ => { if (invalidate) station.bar.availablePatrons.Clear(); return true; }, hub.CheckThread, () => FixedPermissionStamp);
        var author = service.AcquireProvider("author").Provider!;
        int calls = 0;
        author.Register(Definition(), _ => calls++);
        author.ConfigureStation("station", BarRosterOwnership.Exclusive);
        var session = Ready(hub, storage);
        author.Place(session, "contact");
        var plan = service.Plan(session, "station")!;
        var world = new VGModAPI.Core.Integration.BarNativeWorld(typeof(ClickStation), typeof(ClickBar), typeof(ClickPatron),
            new VGModAPI.Core.Integration.BarStationSource(typeof(ClickPlayer), typeof(ClickStation)), _ => true, (_, _) => new ClickPatron(), 5);
        var admission = world.CaptureContact(contact);
        Assert.NotNull(admission);
        invalidate = true;
        Assert.False(service.Interact(plan, plan.Patrons[0], admission));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void RuntimeHostAppliesClicksAndRestoresVanillaAfterProviderRemoval()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var service = new BarContentService(storage, hub,
            (_, _) => new StoryHostPlugin("author", typeof(BarContentServiceTests).Assembly), _ => false, hub.CheckThread);
        var author = service.AcquireProvider("author").Provider!;
        int clicks = 0;
        author.Register(Definition(), _ => clicks++);
        var session = Ready(hub, storage); author.Place(session, "contact");
        var station = new BarNativeSerializationTests.Station();
        var vanilla = new BarNativeSerializationTests.Patron(); station.bar.availablePatrons.Add(vanilla);
        ClickPlayer.current = new ClickPlayer { currentPointOfInterest = station };
        var contacts = new VGModAPI.Core.Integration.BarNativeContacts(typeof(BarNativeSerializationTests.Salesman),
            typeof(BarNativeSerializationTests.Patron), typeof(BarNativeSerializationTests.Station), _ => new UnityEngine.Sprite());
        var world = new VGModAPI.Core.Integration.BarNativeWorld(typeof(BarNativeSerializationTests.Station), typeof(BarNativeSerializationTests.Bar),
            typeof(BarNativeSerializationTests.Patron), new VGModAPI.Core.Integration.BarStationSource(typeof(ClickPlayer), typeof(BarNativeSerializationTests.Station)),
            contacts.IsOwned, contacts.Create, 5);
        var serialization = new VGModAPI.Core.Integration.BarNativeSerialization(typeof(BarNativeSerializationTests.Bar), typeof(BarNativeSerializationTests.Patron),
            typeof(BarNativeSerializationTests.Value), typeof(BarNativeSerializationTests.JsonObject), typeof(BarNativeSerializationTests.JsonArray), contacts, world);
        var host = new VGModAPI.Core.Integration.BarRuntimeHost(service, world, contacts, serialization, id => service.Plan(session, id),
            () => storage.StateReady, () => storage.MutationAllowed, hub.CheckThread, error => throw error);
        Assert.Equal(BarRosterApplyStatus.Applied, host.Reconcile(station.bar));
        var contact = Assert.Single(station.bar.availablePatrons, patron => contacts.IsOwned(patron));
        host.Interact(contact); Assert.Equal(1, clicks);
        Assert.True(host.TrySerialize(station.bar, out _));
        storage.MutationAllowed = false;
        Assert.Equal(BarRosterApplyStatus.Unavailable, host.Reconcile(station.bar));
        host.Interact(contact); Assert.Equal(1, clicks);
        storage.MutationAllowed = true;
        author.Dispose();
        Assert.Equal(BarRosterApplyStatus.Applied, host.Reconcile(station.bar));
        Assert.Same(vanilla, Assert.Single(station.bar.availablePatrons));
        host.Interact(contact); Assert.Equal(1, clicks);
    }

    private static readonly object FixedPermissionStamp = new();
    private sealed class Storage : IPersistenceApi, IPersistenceRegistration, IPersistenceReadiness
    {
        internal PersistenceProvider Provider = null!;
        public bool MutationAllowed { get; set; } = true;
        public bool StateReady { get; set; } = true;
        public string Status => "test";
        public IPersistenceRegistration Register(PersistenceProvider provider) { Provider = provider; return this; }
        public void Dispose() { MutationAllowed = false; StateReady = false; }
    }
    private sealed class World : IBarRosterWorld
    {
        internal int Capacity = 5;
        internal Action? DuringCreate;
        internal int Created;
        internal bool Applied;
        public BarRosterSnapshot Capture(string station) => new(this, Array.Empty<object>(), Capacity);
        public object CreateContact(BarPatronState state) { Created++; DuringCreate?.Invoke(); return new object(); }
        public bool Apply(BarRosterSnapshot snapshot, System.Collections.Generic.IReadOnlyList<object> patrons, Func<bool> stillValid)
        { if (!stillValid()) return false; Applied = true; return true; }
    }

    [Theory]
    [InlineData(0, false, 2)]
    [InlineData(1, false, 0)]
    [InlineData(1, true, 1)]
    public void RosterApplicationRefusesCapacityAndReentrantChanges(int capacity, bool dispose, int expectedStatus)
    {
        var expected = (BarRosterApplyStatus)expectedStatus;
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var service = new BarContentService(storage, hub,
            (_, _) => new StoryHostPlugin("author", typeof(BarContentServiceTests).Assembly), _ => false, hub.CheckThread);
        var author = service.AcquireProvider("author").Provider!;
        author.Register(Definition());
        var session = Ready(hub, storage);
        author.Place(session, "contact");
        var plan = service.Plan(session, "station")!;
        var world = new World { Capacity = capacity, DuringCreate = dispose ? author.Dispose : null };
        Assert.Equal(expected, BarRosterApplication.Apply(service, world, plan));
        Assert.Equal(expected == BarRosterApplyStatus.Applied, world.Applied);
        if (capacity == 0) Assert.Equal(0, world.Created);
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
    public void StationPlansApplyOwnershipAndInvalidateOnProviderChanges()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var service = new BarContentService(storage, hub,
            (plugin, _) => new StoryHostPlugin((string)plugin, typeof(BarContentServiceTests).Assembly), _ => true, hub.CheckThread, () => FixedPermissionStamp);
        var a = service.AcquireProvider("a").Provider!;
        var b = service.AcquireProvider("b").Provider!;
        a.Register(Definition()); b.Register(Definition());
        var session = Ready(hub, storage);
        a.Place(session, "contact"); b.Place(session, "contact");
        var additive = service.Plan(session, "station")!;
        Assert.Equal(2, additive.Patrons.Count);
        Assert.True(additive.Policy.KeepVanilla);
        Assert.True(service.IsCurrent(additive));
        a.ConfigureStation("station", BarRosterOwnership.Exclusive);
        Assert.False(service.IsCurrent(additive));
        var exclusive = service.Plan(session, "station")!;
        Assert.False(exclusive.Policy.KeepVanilla);
        Assert.Equal(a.ProviderId, Assert.Single(exclusive.Patrons).Id.Provider);
        Assert.Equal(BarRosterPolicy.Denial.StationOwnedExclusively, exclusive.Policy.Denied[b.ProviderId]);
        Assert.True(service.Plan(session, "another-station")!.Policy.KeepVanilla);
        b.ConfigureStation("station", BarRosterOwnership.Exclusive);
        var conflict = service.Plan(session, "station")!;
        Assert.Empty(conflict.Patrons);
        Assert.True(conflict.Policy.KeepVanilla);
        b.Dispose();
        Assert.False(service.IsCurrent(conflict));
        Assert.Single(service.Plan(session, "station")!.Patrons);
    }

    [Fact]
    public void MissionResolutionCannotPublishAPlanAfterReentrantMutation()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var service = new BarContentService(storage, hub,
            (_, _) => new StoryHostPlugin("author", typeof(BarContentServiceTests).Assembly), _ => true, hub.CheckThread);
        var provider = service.AcquireProvider("author").Provider!;
        provider.Register(new BarPatronDefinition("contact", "station", "Name", "Description", "seed",
            mission: new StoryContentId(provider.ProviderId, "job"), occurrence: Guid.NewGuid()));
        var session = Ready(hub, storage);
        provider.Place(session, "contact");
        Assert.Null(service.Plan(session, "station"));
        object stamp = new();
        bool ready = true;
        var plan = service.Plan(session, "station", (_, _) => ready, () => stamp)!;
        Assert.NotNull(plan);
        ready = false;
        Assert.False(service.IsCurrent(plan));
        Assert.Null(service.Plan(session, "station", (_, _) => { stamp = new object(); return true; }, () => stamp));
        Assert.Null(service.Plan(session, "station", (_, _) => { provider.Dispose(); return true; }, () => stamp));
        Assert.Empty(service.Plan(session, "station")!.Patrons);
        Assert.Single(BarPatronCodec.Decode(storage.Provider.Capture()));
    }

    [Fact]
    public void PermissionCallbackDisposalCannotPublishAStalePlan()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        Action? duringPermission = null;
        using var service = new BarContentService(storage, hub,
            (_, _) => new StoryHostPlugin("author", typeof(BarContentServiceTests).Assembly),
            _ => { duringPermission?.Invoke(); return true; }, hub.CheckThread, () => FixedPermissionStamp);
        var author = service.AcquireProvider("author").Provider!;
        author.Register(Definition());
        author.ConfigureStation("station", BarRosterOwnership.Exclusive);
        var session = Ready(hub, storage);
        author.Place(session, "contact");
        var plan = service.Plan(session, "station")!;
        duringPermission = author.Dispose;
        Assert.False(service.IsCurrent(plan));
        Assert.True(service.Plan(session, "station")!.Policy.KeepVanilla);
    }

    [Fact]
    public void ResolverInvalidatingAnEarlierDependencyRefusesTheWholePlan()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var service = new BarContentService(storage, hub,
            (_, _) => new StoryHostPlugin("author", typeof(BarContentServiceTests).Assembly), _ => false, hub.CheckThread);
        var author = service.AcquireProvider("author").Provider!;
        foreach (string local in new[] { "first", "second" })
            author.Register(new BarPatronDefinition(local, "station", local, "Description", "seed",
                mission: new StoryContentId(author.ProviderId, local), occurrence: Guid.NewGuid()));
        var session = Ready(hub, storage);
        author.Place(session, "first"); author.Place(session, "second");
        object epoch = new();
        bool firstReady = true;
        Assert.Null(service.Plan(session, "station", (mission, _) =>
        {
            if (mission.LocalId == "second") { firstReady = false; epoch = new object(); return true; }
            return firstReady;
        }, () => epoch));
        Assert.False(firstReady);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevokedOrThrowingPermissionInvalidatesExclusivePlans(bool throws)
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        bool allowed = true;
        using var service = new BarContentService(storage, hub,
            (plugin, _) => new StoryHostPlugin((string)plugin, typeof(BarContentServiceTests).Assembly),
            _ => allowed ? true : throws ? throw new InvalidOperationException("permission unavailable") : false, hub.CheckThread, () => FixedPermissionStamp);
        var a = service.AcquireProvider("a").Provider!;
        var b = service.AcquireProvider("b").Provider!;
        a.Register(Definition()); b.Register(Definition());
        a.ConfigureStation("station", BarRosterOwnership.Exclusive);
        var session = Ready(hub, storage);
        a.Place(session, "contact"); b.Place(session, "contact");
        var original = service.Plan(session, "station")!;
        Assert.False(original.Policy.KeepVanilla);
        allowed = false;
        Assert.False(service.IsCurrent(original));
        var revoked = service.Plan(session, "station")!;
        Assert.True(revoked.Policy.KeepVanilla);
        Assert.Equal(b.ProviderId, Assert.Single(revoked.Patrons).Id.Provider);
        Assert.Equal(BarRosterPolicy.Denial.ExclusivePermissionRequired, revoked.Policy.Denied[a.ProviderId]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackRevokingAnEarlierGrantRefusesPlanAndApplication(bool throughMission)
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        object permissionEpoch = new();
        object missionEpoch = new();
        bool attack = false, grantA = true;
        using var service = new BarContentService(storage, hub,
            (plugin, _) => new StoryHostPlugin((string)plugin, typeof(BarContentServiceTests).Assembly),
            id =>
            {
                if (!throughMission && attack && id == "b") { grantA = false; permissionEpoch = new object(); return false; }
                return id != "a" || grantA;
            }, hub.CheckThread, () => permissionEpoch);
        var a = service.AcquireProvider("a").Provider!;
        var b = service.AcquireProvider("b").Provider!;
        a.Register(throughMission ? new BarPatronDefinition("contact", "station", "Name", "Description", "seed",
            mission: new StoryContentId(a.ProviderId, "job"), occurrence: Guid.NewGuid()) : Definition());
        a.ConfigureStation("station", BarRosterOwnership.Exclusive);
        if (!throughMission) b.ConfigureStation("station", BarRosterOwnership.Exclusive);
        var session = Ready(hub, storage);
        a.Place(session, "contact");
        bool Resolve(StoryContentId _, Guid occurrence)
        {
            if (attack) { grantA = false; permissionEpoch = new object(); }
            return true;
        }
        var original = service.Plan(session, "station", Resolve, () => missionEpoch)!;
        Assert.NotNull(original);
        attack = true;
        Assert.False(service.IsCurrent(original));
        grantA = true;
        Assert.Null(service.Plan(session, "station", Resolve, () => missionEpoch));
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
