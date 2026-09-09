using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VGModAPI.Core;

internal interface IGameplayUiContainerResource : IDisposable
{
    bool IsAlive { get; }
}

internal interface IGameplayUiSurface : IDisposable
{
    bool IsAlive { get; }
    IGameplayUiContainerResource CreateContainer(string pluginId, string localId);
}

/// <summary>Owns UI identities and leases without depending on Unity or discovering native objects.</summary>
internal sealed class GameplayUiService : IGameplayUiService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly ServiceNotifications<GameplayUiChange> _events;
    private readonly IDisposable _lifetime;
    private readonly List<ContainerLease> _containers = new();
    private IGameplayUiSurface? _surface;
    private GameplayUiSnapshot? _current;
    private long _generation;
    private bool _disposed, _refreshing, _releasing, _faultReported, _lost;
    private Exception? _fault;

    internal GameplayUiService(LifecycleHub hub)
    {
        _hub = hub;
        _status = hub.Services.Get("gameplay-ui");
        hub.Services.WatchFault("gameplay-ui", () => Volatile.Read(ref _fault) != null);
        _events = new(hub.CheckThread, hub.ReportSubscriberFailure, hub.EnterServiceDispatch);
        _status.AvailabilityChanged += AvailabilityUpdated;
        _lifetime = hub.Subscribe("vgmodapi.gameplay-ui", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
                Invalidate();
        });
    }

    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public event Action<GameplayUiChange>? Changed
    {
        add
        {
            _hub.CheckThread();
            if (_disposed) throw new ObjectDisposedException(nameof(GameplayUiService));
            _events.Add(value);
        }
        remove => _events.Remove(value);
    }
    public GameplayUiSnapshot? Current
    {
        get
        {
            _hub.CheckThread();
            if (_disposed || _lost || _current == null || !Availability.IsAvailable) return null;
            try
            {
                if (SessionLive(_current.SessionId) && _surface?.IsAlive == true) return _current;
                _lost = true; // Latch target loss without dispatching callbacks from a getter.
                return null;
            }
            catch (Exception error) { LatchFault(error); return null; }
        }
    }

    // Safe from a foreign-thread hook. Status reads close immediately; Refresh delivers on the main thread.
    internal void LatchFault(Exception error) => Interlocked.CompareExchange(ref _fault, error, null);

    internal void SetAvailable(bool available, ServiceUnavailableReason reason = ServiceUnavailableReason.BindingFailed)
    {
        _hub.CheckThread(); if (_disposed) return;
        _hub.SetCapability("gameplay-ui", available,
            available ? "Observed gameplay UI and consumer containers." : "Gameplay UI integration unavailable.", reason);
    }

    private void AvailabilityUpdated(ServiceAvailability status) { if (!status.IsAvailable) Invalidate(); }
    private bool SessionLive(Guid session) => _hub.CurrentSession is { } current && current.Id == session &&
        current.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized;

    /// <summary>Called only at a successful native initialization boundary, never from Refresh.</summary>
    internal void Observe(Guid session, IGameplayUiSurface surface)
    {
        _hub.CheckThread();
        if (surface == null) throw new ArgumentNullException(nameof(surface));
        if (ReferenceEquals(_surface, surface)) return;
        try
        {
            if (_disposed || _releasing || !Availability.IsAvailable || !SessionLive(session) || !surface.IsAlive)
            { surface.Dispose(); return; }
            var generation = _generation + 1;
            Invalidate();
            // Teardown callbacks may start another session, stop the API or observe a newer UI.
            if (_generation != generation || _disposed || !Availability.IsAvailable || !SessionLive(session) || !surface.IsAlive)
            { surface.Dispose(); return; }
            _surface = surface;
            _current = new GameplayUiSnapshot(session);
            _events.Publish(new GameplayUiChange(null, _current));
        }
        catch (Exception error)
        {
            if (!ReferenceEquals(_surface, surface)) SafeDispose(surface);
            Fault(error);
        }
    }

    /// <summary>Teardown-only reconciliation. It never manufactures a ready host.</summary>
    internal void Refresh()
    {
        _hub.CheckThread(); if (_disposed || _refreshing) return;
        var fault = Volatile.Read(ref _fault);
        if (fault != null)
        {
            if (!_faultReported) { _faultReported = true; Report(fault); SetAvailable(false, ServiceUnavailableReason.ObserverFault); }
            return;
        }
        if (_current == null) return;
        _refreshing = true;
        try
        {
            if (_lost || !Availability.IsAvailable || !SessionLive(_current.SessionId) || _surface?.IsAlive != true)
                Invalidate();
            foreach (var container in _containers.ToArray()) if (!container.IsValid) container.Dispose();
        }
        catch (Exception error) { Fault(error); }
        finally { _refreshing = false; }
    }

    internal void Invalidate(IGameplayUiSurface expected)
    {
        _hub.CheckThread();
        if (ReferenceEquals(_surface, expected)) Invalidate();
    }

    private void Invalidate()
    {
        _hub.CheckThread();
        ++_generation;
        var previous = _current; var surface = _surface;
        _current = null; _surface = null; _lost = false;
        var releasing = _releasing;
        _releasing = true;
        try
        {
            foreach (var container in _containers.ToArray()) container.Dispose();
            SafeDispose(surface);
        }
        finally { _releasing = releasing; }
        if (previous != null) _events.Publish(new GameplayUiChange(previous, null));
    }

    internal GameplayUiContainerStatus CreateContainer(GameplayUiSnapshot expected, string pluginId, string localId, out ContainerLease? container)
    {
        _hub.CheckThread();
        if (expected == null) throw new ArgumentNullException(nameof(expected));
        var identity = new RecipeId(pluginId, localId);
        container = null;
        Refresh();
        if (_disposed || !Availability.IsAvailable) return GameplayUiContainerStatus.Unavailable;
        if (_current == null) return GameplayUiContainerStatus.NoHost;
        if (!ReferenceEquals(expected, _current)) return GameplayUiContainerStatus.StaleHost;
        // Drop individually destroyed containers, allowing the consumer to recreate its own identity.
        foreach (var old in _containers.ToArray()) if (!old.IsValid) old.Dispose();
        if (!ReferenceEquals(expected, Current)) return GameplayUiContainerStatus.StaleHost;
        if (_containers.Any(value => value.PluginId == identity.ProviderId && value.LocalId == identity.LocalId))
            return GameplayUiContainerStatus.DuplicateIdentity;
        if (_containers.Count >= 64) return GameplayUiContainerStatus.LimitReached;
        var lease = new ContainerLease(this, expected, identity.ProviderId, identity.LocalId);
        // Reserve before calling Unity, which can invoke consumer MonoBehaviour callbacks reentrantly.
        _containers.Add(lease);
        try
        {
            var resource = _surface!.CreateContainer(identity.ProviderId, identity.LocalId);
            if (resource == null) throw new InvalidOperationException("Container factory returned no resource.");
            lease.Attach(resource);
            if (!lease.IsValid)
            {
                var status = ReferenceEquals(expected, Current) ? GameplayUiContainerStatus.CreationFailed : GameplayUiContainerStatus.StaleHost;
                lease.Dispose(); return status;
            }
            container = lease;
            return GameplayUiContainerStatus.Created;
        }
        catch (Exception error)
        {
            lease.Dispose(); Report(error);
            return GameplayUiContainerStatus.CreationFailed;
        }
    }

    private void Report(Exception error) { try { _hub.ReportSubscriberFailure("vgmodapi.gameplay-ui", error); } catch { } }
    private void Fault(Exception error)
    {
        LatchFault(error);
        if (!_faultReported) { _faultReported = true; Report(error); }
        SetAvailable(false, ServiceUnavailableReason.ObserverFault);
    }
    private void SafeDispose(IDisposable? value) { try { value?.Dispose(); } catch (Exception error) { Report(error); } }

    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true;
        _lifetime.Dispose(); _status.AvailabilityChanged -= AvailabilityUpdated;
        Invalidate();
        _events.Complete();
        _hub.SetCapability("gameplay-ui", false, "Gameplay UI service stopped.", ServiceUnavailableReason.ApiStopped);
    }

    internal sealed class ContainerLease : IDisposable
    {
        private readonly GameplayUiService _owner;
        internal readonly GameplayUiSnapshot Host;
        internal readonly string PluginId, LocalId;
        private IGameplayUiContainerResource? _resource;
        private bool _disposed, _revoked;
        internal ContainerLease(GameplayUiService owner, GameplayUiSnapshot host, string pluginId, string localId)
        { _owner = owner; Host = host; PluginId = pluginId; LocalId = localId; }
        internal void Attach(IGameplayUiContainerResource resource)
        {
            if (_disposed) _owner.SafeDispose(resource); else _resource = resource;
        }
        internal bool IsValid
        {
            get
            {
                _owner._hub.CheckThread();
                if (_disposed || _revoked) return false;
                try
                {
                    if (ReferenceEquals(Host, _owner.Current) && !_disposed && (_resource == null || _resource.IsAlive)) return true;
                }
                catch (Exception error) { _owner.LatchFault(error); }
                _revoked = true;
                return false;
            }
        }
        internal IGameplayUiContainerResource? Resource => IsValid ? _resource : null;
        public void Dispose()
        {
            _owner._hub.CheckThread(); if (_disposed) return;
            _disposed = true;
            var resource = _resource; _resource = null;
            _owner._containers.Remove(this);
            _owner.SafeDispose(resource);
        }
    }
}
