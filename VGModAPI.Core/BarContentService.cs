using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core;

internal sealed partial class BarContentService : IBarService, IDisposable
{
    private readonly StoryHostAuthenticator _authenticate;
    private readonly Func<string, bool> _exclusivePermission;
    private readonly Func<object>? _permissionStamp;
    private readonly ILifecycleService _lifecycle;
    private readonly Action _checkThread;
    private readonly Action<string, Exception>? _reportObserver;
    private readonly BarPatronPersistence? _storage;
    private BarPatronPersistence _persistence => _storage ?? throw new InvalidOperationException("Bar save data unavailable.");
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly ServiceSubscriptions<BarRosterFinalized> _handlers;
    private readonly StoryProviderBindings _bindings = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<BarPatronId, BarPatronState> _transient = new();
    private bool _disposed;

    internal BarContentService(ISaveDataService? persistence, LifecycleHub lifecycle, StoryHostAuthenticator authenticate,
        Func<string, bool> exclusivePermission, Action checkThread, Func<object>? permissionStamp = null, Action<string, Exception>? reportObserver = null)
    {
        _hub = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _status = lifecycle.Services.Get("owned-bars");
        if (persistence == null && Availability.IsAvailable) lifecycle.SetCapability("owned-bars", false, "Bar save data unavailable.");
        _handlers = new ServiceSubscriptions<BarRosterFinalized>(lifecycle, Subscribe,
            fact => Availability.IsAvailable && ((ILifecycleService)_hub).CurrentSession?.Id == fact.SessionId);
        _reportObserver = reportObserver;
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate));
        _exclusivePermission = exclusivePermission ?? throw new ArgumentNullException(nameof(exclusivePermission));
        _permissionStamp = permissionStamp;
        _checkThread = checkThread ?? throw new ArgumentNullException(nameof(checkThread));
        _storage = persistence == null ? null : new BarPatronPersistence(persistence, lifecycle, checkThread);
        try { lifecycle.Changed += OnLifecycle; }
        catch { _storage?.Dispose(); throw; }
    }

    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public event Action<BarRosterFinalized>? RosterFinalized { add => _handlers.Add(value); remove => _handlers.Remove(value); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public BarProviderResult AcquireProvider(object pluginInstance, ISaveDataRegistration? saveData = null)
    {
        _checkThread();
        if (_disposed || _storage == null || !Availability.IsAvailable) return new BarProviderResult(BarStatus.Unavailable, null);
        var caller = Assembly.GetCallingAssembly();
        StoryHostPlugin? plugin;
        try { plugin = _authenticate(pluginInstance, caller); } catch { plugin = null; }
        if (_disposed || _storage == null || !Availability.IsAvailable) return new BarProviderResult(BarStatus.Unavailable, null);
        if (plugin == null) return new BarProviderResult(BarStatus.UnknownPlugin, null);
        if (!ReferenceEquals(plugin.Assembly, caller)) return new BarProviderResult(BarStatus.CallerMismatch, null);
        string segment = StoryProviderIdentity.Segment(plugin);
        var binding = _bindings.Bind(segment, plugin.PluginId);
        if (binding == StoryBindingStatus.Conflict) return new BarProviderResult(BarStatus.ProviderConflict, null);
        if (binding == StoryBindingStatus.LimitExceeded) return new BarProviderResult(BarStatus.LimitExceeded, null);
        if (_leases.ContainsKey(segment)) return new BarProviderResult(BarStatus.AlreadyAcquired, null);
        var lease = new Lease(this, segment, plugin.PluginId, saveData);
        _leases.Add(segment, lease);
        Changed();
        return new BarProviderResult(BarStatus.Succeeded, lease);
    }

    internal bool CanSerializeCurrent()
    {
        _checkThread();
        return !_disposed && _storage != null && Availability.IsAvailable && _lifecycle.CurrentSession is { } session && _persistence.Read(session.Id, out _);
    }
    internal bool CanMutateCurrent()
    {
        _checkThread();
        return !_disposed && _storage != null && Availability.IsAvailable && _lifecycle.CurrentSession is { } session && _persistence.CanMutate(session.Id);
    }

    private bool Active(Lease lease) => !_disposed && _leases.TryGetValue(lease.ProviderId, out var current) && ReferenceEquals(current, lease);
    private BarResult? Guard(Lease lease, Guid session)
    {
        _checkThread();
        if (!Active(lease) || !Availability.IsAvailable || _storage == null) return new BarResult(BarStatus.Unavailable);
        if (_lifecycle.CurrentSession?.Id != session) return new BarResult(BarStatus.GameEnded);
        if (!_persistence.CanMutate(session)) return new BarResult(BarStatus.Unavailable);
        return null;
    }
    private void OnLifecycle(LifecycleEvent value)
    {
        if (_lifecycle.CurrentSession?.Id != value.Session?.Id || _lifecycle.CurrentSession?.Phase != value.Session?.Phase) return;
        if (value.Kind == LifecycleEventKind.SessionStarting || value.Kind == LifecycleEventKind.SessionInvalidated || value.Kind == LifecycleEventKind.SessionStartFailed)
        {
            _game?.Close(); _game = null;
            _transient.Clear();
            Changed();
        }
    }
    public void Dispose()
    {
        _checkThread();
        if (_disposed) return;
        _disposed = true;
        var health = Availability;
        _hub.SetCapability("owned-bars", false, health.IsAvailable ? "Bar service stopped." : health.Detail, health.IsAvailable ? ServiceUnavailableReason.ApiStopped : health.Reason);
        _game?.Close(); _game = null;
        _handlers.Dispose();
        Changed();
        foreach (var lease in _leases.Values) { foreach (var registration in lease.Registrations.Values) registration.Close(); lease.Registrations.Clear(); lease.Definitions.Clear(); lease.Stations.Clear(); lease.Interactions.Clear(); }
        _leases.Clear(); _transient.Clear();
        foreach (var observer in _observers) observer.Active = false;
        _observers.Clear();
        _lifecycle.Changed -= OnLifecycle; _storage?.Dispose();
    }
}
