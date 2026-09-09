using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class GameplayUiServiceTests : IDisposable
{
    private readonly List<Exception> _errors = new();
    private readonly LifecycleHub _hub;
    private readonly GameplayUiService _service;
    private readonly Guid _session;
    public GameplayUiServiceTests()
    {
        _hub = new((_, error) => _errors.Add(error));
        _hub.SetCapability("session-lifecycle", true, "Test binding.");
        _service = new GameplayUiService(_hub);
        _service.SetAvailable(true);
        _session = _hub.Begin(SessionOrigin.NewGame, null);
        _hub.PlayerReady(_session);
    }

    [Fact]
    public void ReadinessRequiresObservedSurfaceNotGameplayOrPollingAndLateSubscribersCanQuery()
    {
        IGameplayUiService api = _service;
        Assert.True(api.Availability.IsAvailable);
        _hub.GameplayInitialized(_session);
        _service.Refresh(); Assert.Null(api.Current);
        var changes = new List<GameplayUiChange>(); api.Changed += changes.Add;
        var surface = new Surface(); _service.Observe(_session, surface);
        var ready = Assert.IsType<GameplayUiSnapshot>(api.Current);
        Assert.Equal(_session, ready.SessionId); Assert.NotEqual(Guid.Empty, ready.Id);
        Assert.Null(Assert.Single(changes).Previous); Assert.Same(ready, changes[0].Current);
        _service.Observe(_session, surface); _service.Refresh(); Assert.Single(changes);
        var late = 0; api.Changed += _ => late++;
        Assert.Same(ready, api.Current); Assert.Equal(0, late);
    }

    [Fact]
    public void PlayerReadyUiDoesNotWaitForUnrelatedGameplayManagerStart()
    {
        _service.Observe(_session, new Surface());
        var ready = _service.Current; Assert.NotNull(ready);
        _hub.GameplayInitialized(_session);
        Assert.Same(ready, _service.Current);
    }

    [Fact]
    public void ReplacementRevokesContainersBeforeTeardownThenPublishesNewIdentity()
    {
        var oldSurface = new Surface(); _service.Observe(_session, oldSurface);
        var oldHost = _service.Current!;
        Assert.Equal(GameplayUiContainerStatus.Created, _service.CreateContainer(oldHost, "one", "windows", out var lease));
        var changes = new List<GameplayUiChange>();
        _service.Changed += change =>
        {
            changes.Add(change);
            if (change.Previous != null)
            {
                Assert.Null(_service.Current); Assert.False(lease!.IsValid);
                Assert.True(oldSurface.Disposed); Assert.True(Assert.Single(oldSurface.Resources).Disposed);
            }
        };
        var next = new Surface(); _service.Observe(_session, next);
        Assert.Equal(2, changes.Count); Assert.Same(oldHost, changes[0].Previous); Assert.Null(changes[0].Current);
        Assert.Null(changes[1].Previous); Assert.Same(_service.Current, changes[1].Current);
        Assert.NotEqual(oldHost.Id, _service.Current!.Id);
        _service.Invalidate(oldSurface); Assert.Same(changes[1].Current, _service.Current);
        Assert.Equal(GameplayUiContainerStatus.StaleHost, _service.CreateContainer(oldHost, "one", "late", out var stale));
        Assert.Null(stale); Assert.Empty(next.Resources);
    }

    [Fact]
    public void SessionReplacementAndMenuInvalidationNeverResurrectOldUi()
    {
        var old = new Surface(); _service.Observe(_session, old);
        var host = _service.Current!;
        _service.CreateContainer(host, "one", "windows", out var lease);
        var replacement = _hub.Begin(SessionOrigin.SaveLoad, "same-save");
        Assert.Null(_service.Current); Assert.False(lease!.IsValid); Assert.True(old.Disposed);
        var premature = new Surface(); _service.Observe(replacement, premature);
        Assert.Null(_service.Current); Assert.True(premature.Disposed);
        _hub.PlayerReady(replacement);
        var stale = new Surface(); _service.Observe(_session, stale);
        Assert.Null(_service.Current); Assert.True(stale.Disposed);
        _service.Observe(replacement, new Surface()); Assert.NotNull(_service.Current);
        _hub.Invalidate("menu"); Assert.Null(_service.Current);
        _service.Refresh(); Assert.Null(_service.Current);
        Assert.Equal(GameplayUiContainerStatus.NoHost, _service.CreateContainer(host, "one", "windows", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void NativeDestructionAndInvalidationAreIdempotentAndNeverCreateReadiness()
    {
        var changes = new List<GameplayUiChange>(); _service.Changed += changes.Add;
        var surface = new Surface(); _service.Observe(_session, surface);
        _service.CreateContainer(_service.Current!, "one", "windows", out var lease);
        surface.Alive = false;
        Assert.False(lease!.IsValid); Assert.Null(_service.Current);
        Assert.Single(changes); // Queries close access but do not pump consumer callbacks.
        _service.Invalidate(surface); _service.Refresh(); Assert.Equal(2, changes.Count);
        surface.Alive = true; _service.Refresh(); Assert.Null(_service.Current); Assert.False(lease.IsValid);
    }

    [Fact]
    public void ContainersCanBeCreatedLaterAndDisposalIsIsolatedByProviderAndLocalIdentity()
    {
        var surface = new Surface(); _service.Observe(_session, surface);
        var host = _service.Current!;
        Assert.Equal(GameplayUiContainerStatus.Created, _service.CreateContainer(host, "one", "window", out var one));
        Assert.Equal(GameplayUiContainerStatus.Created, _service.CreateContainer(host, "two", "window", out var two));
        Assert.Equal(GameplayUiContainerStatus.DuplicateIdentity, _service.CreateContainer(host, "one", "window", out var duplicate));
        Assert.Null(duplicate); Assert.Equal(2, surface.Resources.Count);
        _service.Refresh(); // A later input handler, not the readiness callback.
        Assert.Equal(GameplayUiContainerStatus.Created, _service.CreateContainer(host, "one", "dialog", out var dialog));
        one!.Dispose(); one.Dispose(); Assert.False(one.IsValid); Assert.Null(one.Resource);
        Assert.True(two!.IsValid); Assert.True(dialog!.IsValid);
        Assert.True(surface.Resources[0].Disposed); Assert.False(surface.Resources[1].Disposed);
        Assert.Equal(GameplayUiContainerStatus.Created, _service.CreateContainer(host, "one", "window", out var recreated));
        Assert.True(recreated!.IsValid); Assert.False(one.IsValid);
    }

    [Fact]
    public void MissingSignalAndUnavailableBindingRefuseCleanly()
    {
        _service.Observe(_session, new Surface()); var host = _service.Current!;
        _service.SetAvailable(false, ServiceUnavailableReason.UnsupportedGame);
        Assert.Null(_service.Current);
        Assert.Equal(ServiceUnavailableReason.UnsupportedGame, _service.Availability.Reason);
        Assert.Equal(GameplayUiContainerStatus.Unavailable, _service.CreateContainer(host, "one", "window", out var missing));
        Assert.Null(missing);
        var refused = new Surface(); _service.Observe(_session, refused); Assert.True(refused.Disposed);
        _service.SetAvailable(true); Assert.Null(_service.Current);
        Assert.Equal(GameplayUiContainerStatus.NoHost, _service.CreateContainer(host, "one", "window", out missing));
        Assert.Null(missing);
    }

    [Fact]
    public void DependencyFailureRevokesHostAndItsLeases()
    {
        _service.Observe(_session, new Surface());
        _service.CreateContainer(_service.Current!, "one", "windows", out var lease);
        _hub.SetCapability("session-lifecycle", false, "Observer fault.", ServiceUnavailableReason.ObserverFault);
        Assert.False(_service.Availability.IsAvailable); Assert.Null(_service.Current); Assert.False(lease!.IsValid);
        _hub.SetCapability("session-lifecycle", true, "Restored."); Assert.Null(_service.Current);
    }

    [Fact]
    public void SubscriberFaultRemovalAndReentrantShutdownPreserveTerminalDelivery()
    {
        var seen = new List<string>();
        Action<GameplayUiChange> removed = _ => seen.Add("removed");
        _service.Changed += change =>
        {
            seen.Add(change.Current == null ? "a:gone" : "a:ready");
            _service.Changed -= removed;
            if (change.Current != null) _service.Dispose();
            throw new InvalidOperationException("consumer");
        };
        _service.Changed += removed;
        _service.Changed += change => seen.Add(change.Current == null ? "b:gone" : "b:ready");
        _service.Observe(_session, new Surface());
        Assert.Equal(new[] { "a:ready", "b:ready", "a:gone", "b:gone" }, seen);
        Assert.Equal(2, _errors.Count);
        Assert.All(_errors, error => Assert.Equal("consumer", Assert.IsType<InvalidOperationException>(error).Message));
        _errors.Clear(); Assert.Null(_service.Current);
        Assert.Throws<ObjectDisposedException>(() => _service.Changed += removed);
    }

    [Fact]
    public void ReentrantReplacementDuringTeardownWinsOverOlderObservation()
    {
        _service.Observe(_session, new Surface());
        var winner = new Surface(); var older = new Surface(); var reentered = false;
        _service.Changed += change =>
        {
            if (change.Previous == null || reentered) return;
            reentered = true; _service.Observe(_session, winner);
        };
        _service.Observe(_session, older);
        Assert.True(older.Disposed); Assert.False(winner.Disposed);
        Assert.Equal(GameplayUiContainerStatus.Created, _service.CreateContainer(_service.Current!, "one", "windows", out _));
        Assert.Single(winner.Resources); Assert.Empty(older.Resources);
    }

    [Fact]
    public void InvalidationDuringFactoryRollsBackAndCannotRemoveNewHostContainer()
    {
        var old = new Surface(); _service.Observe(_session, old); var oldHost = _service.Current!;
        GameplayUiService.ContainerLease? newer = null;
        old.Creating = () =>
        {
            _service.Observe(_session, new Surface());
            Assert.Equal(GameplayUiContainerStatus.Created, _service.CreateContainer(_service.Current!, "one", "window", out newer));
        };
        Assert.Equal(GameplayUiContainerStatus.StaleHost, _service.CreateContainer(oldHost, "one", "window", out var stale));
        Assert.Null(stale); Assert.True(Assert.Single(old.Resources).Disposed); Assert.True(newer!.IsValid);
        Assert.Equal(GameplayUiContainerStatus.DuplicateIdentity, _service.CreateContainer(_service.Current!, "one", "window", out _));
    }

    [Fact]
    public void CreationFailureReleasesReservationAndReentrantDuplicateDoesNotPartiallyAttach()
    {
        var surface = new Surface(); _service.Observe(_session, surface); var host = _service.Current!;
        surface.Creating = () =>
        {
            Assert.Equal(GameplayUiContainerStatus.DuplicateIdentity, _service.CreateContainer(host, "one", "window", out var duplicate));
            Assert.Null(duplicate); throw new InvalidOperationException("factory failure");
        };
        Assert.Equal(GameplayUiContainerStatus.CreationFailed, _service.CreateContainer(host, "one", "window", out var failed));
        Assert.Null(failed); Assert.Empty(surface.Resources);
        surface.Creating = null;
        Assert.Equal(GameplayUiContainerStatus.Created, _service.CreateContainer(host, "one", "window", out _));
        Assert.Single(surface.Resources);
        Assert.Equal("factory failure", Assert.IsType<InvalidOperationException>(Assert.Single(_errors)).Message);
        _errors.Clear();
    }

    [Fact]
    public void IndividuallyDestroyedResourceRevokesLeaseWithoutInvalidatingOtherConsumers()
    {
        var surface = new Surface(); _service.Observe(_session, surface); var host = _service.Current!;
        _service.CreateContainer(host, "one", "window", out var first);
        _service.CreateContainer(host, "two", "window", out var second);
        surface.Resources[0].Alive = false;
        Assert.False(first!.IsValid); Assert.True(second!.IsValid); Assert.Same(host, _service.Current);
        surface.Resources[0].Alive = true; Assert.False(first.IsValid); // A revoked lease never recovers.
        Assert.Equal(GameplayUiContainerStatus.Created, _service.CreateContainer(host, "one", "window", out _));
    }

    [Fact]
    public void QueriedTargetLossCannotRecoverBeforeTheNextUpdate()
    {
        var surface = new Surface(); _service.Observe(_session, surface);
        var notifications = 0; _service.Changed += _ => notifications++;
        surface.Alive = false; Assert.Null(_service.Current);
        surface.Alive = true; Assert.Null(_service.Current);
        Assert.Equal(0, notifications);
        _service.Refresh(); Assert.Equal(1, notifications); Assert.True(surface.Disposed);
        Assert.Null(_service.Current);
    }

    [Fact]
    public void CapacityAndInvalidArgumentsDoNotCreateResources()
    {
        var surface = new Surface(); _service.Observe(_session, surface); var host = _service.Current!;
        Assert.Throws<ArgumentNullException>(() => _service.CreateContainer(null!, "one", "window", out _));
        Assert.Throws<ArgumentException>(() => _service.CreateContainer(host, "", "window", out _));
        for (var i = 0; i < 64; i++) Assert.Equal(GameplayUiContainerStatus.Created, _service.CreateContainer(host, "one", "window" + i, out _));
        Assert.Equal(GameplayUiContainerStatus.LimitReached, _service.CreateContainer(host, "two", "window", out var refused));
        Assert.Null(refused); Assert.Equal(64, surface.Resources.Count);
    }

    [Fact]
    public async Task WrongThreadAccessIsRejectedWithoutChangingState()
    {
        _service.Observe(_session, new Surface()); var host = _service.Current!;
        _service.CreateContainer(host, "one", "windows", out var lease);
        await Task.Run(() =>
        {
            Assert.Throws<InvalidOperationException>(() => _service.Current);
            Assert.Throws<InvalidOperationException>(() => _service.CreateContainer(host, "two", "windows", out _));
            Assert.Throws<InvalidOperationException>(() => lease!.Dispose());
        });
    }

    [Fact]
    public void ApiShutdownInvalidatesWithoutDependingOnNativeSceneDestruction()
    {
        var surface = new Surface(); _service.Observe(_session, surface);
        var host = _service.Current!; _service.CreateContainer(host, "one", "windows", out var lease);
        _hub.Services.AfterStopped(_service.Dispose);
        _hub.Dispose();
        Assert.Null(_service.Current); Assert.False(lease!.IsValid); Assert.True(surface.Disposed);
        Assert.True(Assert.Single(surface.Resources).Disposed);
        Assert.Equal(GameplayUiContainerStatus.Unavailable, _service.CreateContainer(host, "one", "windows", out _));
        _service.Dispose(); lease.Dispose();
    }

    [Fact]
    public void FaultLatchClosesAccessBeforeMainThreadNotificationAndNeverRecovers()
    {
        _service.Observe(_session, new Surface()); var host = _service.Current!;
        _service.CreateContainer(host, "one", "windows", out var lease);
        var changes = 0; _service.Changed += _ => changes++;
        Assert.Null(ServiceNotificationTests.OnWorker(() => _service.LatchFault(new InvalidOperationException("foreign hook"))));
        Assert.Equal(ServiceUnavailableReason.ObserverFault, _service.Availability.Reason);
        Assert.Null(_service.Current); Assert.False(lease!.IsValid); Assert.Equal(0, changes);
        _service.Refresh(); Assert.Equal(1, changes); Assert.Single(_errors);
        _service.Refresh(); Assert.Single(_errors);
        _service.SetAvailable(true);
        Assert.False(_service.Availability.IsAvailable); Assert.Null(_service.Current);
        Assert.Equal("foreign hook", Assert.IsType<InvalidOperationException>(Assert.Single(_errors)).Message);
        _errors.Clear();
    }

    // Assertions in isolated callbacks must fail the test rather than being mistaken for expected consumer failures.
    public void Dispose() => Assert.Empty(_errors);

    private sealed class Surface : IGameplayUiSurface
    {
        internal bool Alive = true, Disposed;
        internal Action? Creating;
        internal readonly List<Resource> Resources = new();
        public bool IsAlive => Alive && !Disposed;
        public IGameplayUiContainerResource CreateContainer(string pluginId, string localId)
        {
            Creating?.Invoke();
            var resource = new Resource(); Resources.Add(resource); return resource;
        }
        public void Dispose() { Disposed = true; }
    }
    private sealed class Resource : IGameplayUiContainerResource
    {
        internal bool Alive = true, Disposed;
        public bool IsAlive => Alive && !Disposed;
        public void Dispose() { Disposed = true; }
    }
}
