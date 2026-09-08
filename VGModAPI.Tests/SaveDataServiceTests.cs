using System;
using System.Collections.Generic;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class SaveDataServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vg-typed-data-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static LifecycleHub Bound()
    {
        var hub = new LifecycleHub((_, _) => { });
        foreach (var name in new[] { "session-lifecycle", "save-outcomes", "save-data" }) hub.SetCapability(name, true, "Bound.");
        return hub;
    }
    private PersistenceService Source(LifecycleHub hub) => new(hub, new GenerationStore(_root), path => path, _ => new string('a', 64));
    private static PersistenceProvider Provider(string owner = "owner", Action<SessionSnapshot, byte[]?>? restore = null, Func<byte[]>? capture = null)
        => new(owner, 1, capture ?? (() => new byte[] { 1 }), restore ?? ((_, _) => { }), bytes => bytes.Length == 1);
    private static void Save(LifecycleHub hub, LifecycleEventKind end)
    {
        var operation = Guid.NewGuid();
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, operation, "slot"));
        hub.Publish(new LifecycleEvent(end, hub.CurrentSession, operation, "slot"));
    }

    [Fact]
    public void ProviderLifetimeSeparatesRestorationReadabilityAndTransientMutationGates()
    {
        using var hub = Bound();
        using var source = Source(hub);
        var service = new SaveDataServiceView(hub, source);
        using var registration = service.Register(Provider()).Registration!;
        var states = new List<SaveDataState>();
        var writes = new List<bool>();
        registration.StateChanged += state => { states.Add(state); writes.Add(registration.CanMutate); };
        Assert.Equal(SaveDataStateKind.Inactive, registration.State.Kind);
        Assert.Empty(states);
        var id = hub.Begin(SessionOrigin.NewGame, null);
        Assert.Equal(SaveDataStateKind.Restoring, registration.State.Kind);
        hub.PlayerReady(id);
        Assert.True(registration.CanRead);
        Assert.False(registration.CanMutate);
        hub.GameplayInitialized(id);
        Assert.True(registration.CanMutate);
        Assert.Equal(2, states.Count);
        var operation = Guid.NewGuid();
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, operation, "slot"));
        Assert.True(registration.CanRead);
        Assert.False(registration.CanMutate);
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSkipped, hub.CurrentSession, operation, "slot"));
        Assert.True(registration.CanMutate);
        Assert.Equal(2, states.Count);
        Assert.Equal(new[] { false, false }, writes);
        var next = hub.Begin(SessionOrigin.NewGame, null);
        Assert.Equal(next, registration.State.SessionId);
        Assert.False(registration.CanRead);
    }

    [Fact]
    public void RefusalsAreTypedAndTheLegacyExceptionContractRemains()
    {
        using var hub = Bound();
        using var source = Source(hub);
        var service = new SaveDataServiceView(hub, source);
        Assert.Equal(SaveDataRegistrationStatus.InvalidProvider, service.Register(Provider("BAD ID")).Status);
        Assert.Equal(SaveDataRegistrationStatus.Registered, service.Register(Provider()).Status);
        Assert.Equal(SaveDataRegistrationStatus.DuplicateProvider, service.Register(Provider()).Status);
        Assert.Throws<InvalidOperationException>(() => source.Register(Provider()));
        hub.Begin(SessionOrigin.NewGame, null);
        Assert.Equal(SaveDataRegistrationStatus.SessionAlreadyStarted, service.Register(Provider("another")).Status);
        source.Dispose();
        Assert.Equal(SaveDataRegistrationStatus.Unavailable, service.Register(Provider()).Status);
    }

    [Fact]
    public void CapacityRefusalIsDistinct()
    {
        using var hub = Bound();
        using var source = Source(hub);
        var service = new SaveDataServiceView(hub, source);
        for (var index = 0; index < GenerationStore.MaxOwners; index++)
            Assert.True(service.Register(Provider("owner" + index)).Succeeded);
        Assert.Equal(SaveDataRegistrationStatus.LimitExceeded, service.Register(Provider("overflow")).Status);
    }

    [Fact]
    public void RestoreAndCaptureFailuresHaveTypedReasonsAndCaptureCanRecover()
    {
        using var hub = Bound();
        using var source = Source(hub);
        var service = new SaveDataServiceView(hub, source);
        using var brokenRestore = service.Register(Provider("restore", (_, _) => throw new IOException())).Registration!;
        var captureFails = true;
        using var capture = service.Register(Provider("capture", capture: () => captureFails ? throw new IOException() : new byte[] { 1 })).Registration!;
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id);
        Assert.Equal(SaveDataBlockReason.RestoreFailed, brokenRestore.State.Reason);
        Save(hub, LifecycleEventKind.SaveSucceeded);
        Assert.Equal(SaveDataBlockReason.CaptureFailed, capture.State.Reason);
        Assert.False(capture.CanRead);
        captureFails = false;
        Save(hub, LifecycleEventKind.SaveSucceeded);
        Assert.Equal(SaveDataStateKind.Ready, capture.State.Kind);
        Assert.True(capture.CanRead);
        Assert.True(capture.CanMutate);
        Assert.Equal(SaveDataBlockReason.RestoreFailed, brokenRestore.State.Reason);
    }

    [Fact]
    public void RemovalDuringNotificationClosesOtherProvidersBeforeTheirNextCallback()
    {
        using var hub = Bound();
        using var source = Source(hub);
        var service = new SaveDataServiceView(hub, source);
        using var first = service.Register(Provider("first")).Registration!;
        using var second = service.Register(Provider("second")).Registration!;
        first.StateChanged += state => { if (state.Kind == SaveDataStateKind.Ready) first.Dispose(); };
        var states = new List<SaveDataBlockReason>();
        second.StateChanged += _ => states.Add(second.State.Reason);
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id);
        Assert.Equal(SaveDataStateKind.Disposed, first.State.Kind);
        Assert.Equal(SaveDataBlockReason.ProviderRemoved, second.State.Reason);
        Assert.False(second.CanRead);
        Assert.Equal(new[] { SaveDataBlockReason.None, SaveDataBlockReason.ProviderRemoved, SaveDataBlockReason.ProviderRemoved }, states);
    }

    [Fact]
    public void ReentrantServiceDisposalDrainsProviderDisposedNotifications()
    {
        using var hub = Bound();
        using var source = Source(hub);
        var service = new SaveDataServiceView(hub, source);
        using var first = service.Register(Provider("first")).Registration!;
        using var second = service.Register(Provider("second")).Registration!;
        first.StateChanged += state => { if (state.Kind == SaveDataStateKind.Ready) source.Dispose(); };
        var states = new List<SaveDataStateKind>();
        second.StateChanged += state => states.Add(state.Kind);
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id);
        Assert.Equal(SaveDataStateKind.Disposed, states[states.Count - 1]);
        Assert.Equal(SaveDataStateKind.Disposed, second.State.Kind);
        Assert.False(second.CanRead);
        Assert.False(second.CanMutate);
        Assert.False(hub.IsDispatchingCallbacks);
    }

    [Fact]
    public void ApiShutdownClosesRetainedHandlesAndForeignThreadsCannotAccessThem()
    {
        using var hub = Bound();
        using var source = Source(hub);
        using var registration = new SaveDataServiceView(hub, source).Register(Provider()).Registration!;
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => _ = registration.State));
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(registration.Dispose));
        hub.Dispose();
        Assert.Equal(SaveDataStateKind.Disposed, registration.State.Kind);
        Assert.False(registration.CanRead);
        Assert.False(registration.CanMutate);
        Assert.Throws<ObjectDisposedException>(() => registration.StateChanged += _ => { });
    }
}
