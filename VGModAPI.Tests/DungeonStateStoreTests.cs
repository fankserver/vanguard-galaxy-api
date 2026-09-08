using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonStateStoreTests
{
    private sealed class Persistence : IPersistenceApi, IPersistenceRegistration, IPersistenceReadiness
    {
        internal PersistenceProvider Provider = null!;
        public bool MutationAllowed { get; set; }
        public bool StateReady { get; set; }
        public string Status => StateReady ? "ready" : "unavailable";
        public IPersistenceRegistration Register(PersistenceProvider provider) { Provider = provider; return this; }
        public void Dispose() { StateReady = MutationAllowed = false; }
    }
    private static DungeonOccurrence Occurrence() => new(Guid.NewGuid(), new("mod", "dungeon"), new DungeonDefinition(1, "Dungeon", new DungeonLayout(new[]
    {
        new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "room" }),
        new DungeonCompartmentDefinition("room", CompartmentType.Corridor, new[] { "entry" })
    }), events: new[] { new DungeonEventDefinition("event", "room", "Choose", new[] { new DungeonChoiceDefinition("choice", "Choose") }) }));
    [Fact]
    public void CreationRefusesMissingUnrestoredAndReadOnlyPersistence()
    {
        using var hub = new LifecycleHub((_, _) => { }); using var absent = new DungeonStateStore(hub, null);
        Assert.False(absent.Add(Occurrence()));
        var persistence = new Persistence(); using var store = new DungeonStateStore(hub, persistence);
        Assert.Throws<InvalidOperationException>(() => persistence.Provider.Capture());
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session);
        Assert.False(store.Add(Occurrence()));
        persistence.Provider.Restore(hub.CurrentSession!, null); persistence.StateReady = true;
        Assert.True(store.StateReady); Assert.False(store.Add(Occurrence()));
        persistence.MutationAllowed = true; Assert.True(store.Add(Occurrence()));
    }
    [Fact]
    public void NestedNativeSerializationBlocksMutationButPreservesReadableState()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new Persistence(); using var store = new DungeonStateStore(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session);
        persistence.Provider.Restore(hub.CurrentSession!, null); persistence.StateReady = persistence.MutationAllowed = true;
        var occurrence = Occurrence(); Assert.True(store.Add(occurrence));
        store.BeginSerialization(); store.BeginSerialization();
        Assert.True(store.StateReady); Assert.Single(store.Entries);
        Assert.False(store.Choose(occurrence.Id, "mod", "event", "choice")); Assert.False(store.Add(Occurrence()));
        store.EndSerialization(); Assert.False(store.MutationAllowed);
        store.EndSerialization(); Assert.True(store.Choose(occurrence.Id, "mod", "event", "choice"));
    }
    [Fact]
    public void OwnedChoicesRoundTripAndCrossSlotStateDoesNotLeak()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new Persistence(); using var store = new DungeonStateStore(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "first"); hub.PlayerReady(session);
        persistence.Provider.Restore(hub.CurrentSession!, null); persistence.StateReady = persistence.MutationAllowed = true;
        var occurrence = Occurrence(); Assert.True(store.Add(occurrence));
        Assert.False(store.Choose(occurrence.Id, "foreign", "event", "choice"));
        Assert.True(store.Choose(occurrence.Id, "mod", "event", "choice")); Assert.False(store.Choose(occurrence.Id, "mod", "event", "choice"));
        var saved = persistence.Provider.Capture();
        var next = hub.Begin(SessionOrigin.SaveLoad, "second"); hub.PlayerReady(next);
        Assert.False(store.StateReady); Assert.Empty(store.Entries);
        persistence.Provider.Restore(hub.CurrentSession!, null); Assert.Empty(store.Entries);
        persistence.Provider.Restore(hub.CurrentSession!, saved);
        Assert.Equal("choice", store.Get(occurrence.Id)!.Choices["event"]);
    }
}
