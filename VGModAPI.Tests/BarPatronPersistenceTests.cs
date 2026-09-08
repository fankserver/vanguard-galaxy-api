using System;
using System.IO;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarPatronPersistenceTests
{
    private sealed class Storage : TestSaveDataService
    {
        internal PersistenceProvider Provider = null!;
        public bool MutationAllowed { get; set; } = true;
        public bool StateReady { get; set; } = true;
        public string Status => "test storage";
        public override bool CanRead => StateReady;
        public override bool CanMutate => StateReady && MutationAllowed;
        public override SaveDataRegistrationResult Register(PersistenceProvider provider) { Provider = provider; return new(SaveDataRegistrationStatus.Registered, this); }
        public override void Dispose() { MutationAllowed = false; StateReady = false; }
    }
    private static BarPatronState Row() => new(new BarPatronId("author", "contact"), "station", "Name", "Description", "seed");
    private static Guid Ready(LifecycleHub hub, Storage storage, byte[]? payload = null)
    {
        hub.SetCapability("session-lifecycle", true, "Bound.");
        hub.SetCapability("save-outcomes", true, "Bound.");
        var session = hub.Begin(SessionOrigin.SaveLoad, "fixture.save");
        hub.PlayerReady(session);
        storage.Provider.Restore(hub.CurrentSession!, payload);
        hub.GameplayInitialized(session);
        return session;
    }

    [Fact]
    public void ModuleRegistersAutomaticStorageAndRestoresAnOlderSnapshot()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var module = new BarPatronPersistence(storage, hub, hub.CheckThread);
        Assert.Equal(BarPatronCodec.Owner, storage.Provider.Owner);
        Assert.Equal(BarPatronCodec.SchemaVersion, storage.Provider.SchemaVersion);
        var first = Ready(hub, storage);
        Assert.True(module.Put(first, "author", Row()));
        var snapshot = storage.Provider.Capture();
        Assert.True(storage.Provider.Validate(snapshot));
        Assert.True(module.Remove(first, "author", Row().Id));
        var second = Ready(hub, storage, snapshot);
        Assert.False(module.Read(first, out _));
        Assert.True(module.Read(second, out var rows));
        Assert.Single(rows);
        Assert.Equal(snapshot, storage.Provider.Capture());
    }

    [Fact]
    public void SaveInFlightAllowsReadButNotMutationAndBlockedStateAllowsNeither()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var module = new BarPatronPersistence(storage, hub, hub.CheckThread);
        var session = Ready(hub, storage);
        Assert.True(module.Put(session, "author", Row()));
        storage.MutationAllowed = false;
        Assert.True(module.Read(session, out _));
        Assert.False(module.Remove(session, "author", Row().Id));
        storage.StateReady = false;
        Assert.False(module.Read(session, out _));
        Assert.False(module.Put(session, "author", Row()));
    }

    [Fact]
    public void MalformedRestoreCannotPublishThePreviousSessionsState()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var module = new BarPatronPersistence(storage, hub, hub.CheckThread);
        var first = Ready(hub, storage);
        Assert.True(module.Put(first, "author", Row()));
        var second = hub.Begin(SessionOrigin.SaveLoad, "corrupt.save");
        hub.PlayerReady(second);
        Assert.Throws<InvalidDataException>(() => storage.Provider.Restore(hub.CurrentSession!, Array.Empty<byte>()));
        hub.GameplayInitialized(second);
        Assert.False(module.Read(second, out _));
        Assert.Throws<InvalidOperationException>(() => storage.Provider.Capture());
        Assert.False(module.Put(second, "author", Row()));
    }

    [Fact]
    public void OlderSessionSignalsAndRestoreCannotDisruptCurrentState()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var module = new BarPatronPersistence(storage, hub, hub.CheckThread);
        var first = Ready(hub, storage);
        var oldSession = hub.CurrentSession!;
        var second = Ready(hub, storage);
        Assert.True(module.Put(second, "author", Row()));
        var before = storage.Provider.Capture();
        foreach (var signal in new[] {
            new LifecycleEvent(LifecycleEventKind.SessionStarting, new SessionSnapshot(first, SessionPhase.Starting, SessionOrigin.SaveLoad, "old.save")),
            new LifecycleEvent(LifecycleEventKind.SessionInvalidated, new SessionSnapshot(first, SessionPhase.Invalidated, SessionOrigin.SaveLoad, "old.save")),
            new LifecycleEvent(LifecycleEventKind.SessionStartFailed, new SessionSnapshot(first, SessionPhase.Failed, SessionOrigin.SaveLoad, "old.save")),
            new LifecycleEvent(LifecycleEventKind.SessionStarting, new SessionSnapshot(second, SessionPhase.Starting, SessionOrigin.SaveLoad, "fixture.save"))
        }) hub.Publish(signal);
        Assert.Throws<InvalidOperationException>(() => storage.Provider.Restore(oldSession, null));
        Assert.True(module.Read(second, out var rows));
        Assert.Single(rows);
        Assert.Equal(before, storage.Provider.Capture());
    }

    [Fact]
    public void InvalidationAndDisposalRefuseStaleState()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var storage = new Storage();
        using var module = new BarPatronPersistence(storage, hub, hub.CheckThread);
        var session = Ready(hub, storage);
        Assert.False(module.Put(session, "foreign", Row()));
        Assert.True(module.Put(session, "author", Row()));
        hub.Invalidate("menu");
        Assert.False(module.Read(session, out _));
        Assert.Throws<InvalidOperationException>(() => storage.Provider.Capture());
        session = Ready(hub, storage);
        module.Dispose();
        Assert.False(module.Read(session, out _));
        Assert.False(storage.StateReady);
    }
}
