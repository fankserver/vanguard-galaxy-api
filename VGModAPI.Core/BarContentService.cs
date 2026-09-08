using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core;

internal sealed partial class BarContentService : IBarApi, IDisposable
{
    private readonly StoryHostAuthenticator _authenticate;
    private readonly Func<string, bool> _exclusivePermission;
    private readonly ILifecycleApi _lifecycle;
    private readonly Action _checkThread;
    private readonly BarPatronPersistence _persistence;
    private readonly StoryProviderBindings _bindings = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<BarPatronId, BarPatronState> _transient = new();
    private readonly IDisposable _subscription;
    private bool _disposed;

    internal BarContentService(IPersistenceApi persistence, ILifecycleApi lifecycle, StoryHostAuthenticator authenticate,
        Func<string, bool> exclusivePermission, Action checkThread)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate));
        _exclusivePermission = exclusivePermission ?? throw new ArgumentNullException(nameof(exclusivePermission));
        _checkThread = checkThread ?? throw new ArgumentNullException(nameof(checkThread));
        _persistence = new BarPatronPersistence(persistence, lifecycle, checkThread);
        try { _subscription = lifecycle.Subscribe("vgmodapi.bar-content", OnLifecycle); }
        catch { _persistence.Dispose(); throw; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public BarProviderResult AcquireProvider(object pluginInstance)
    {
        _checkThread();
        if (_disposed) return new BarProviderResult(BarStatus.Unavailable, null);
        var caller = Assembly.GetCallingAssembly();
        StoryHostPlugin? plugin;
        try { plugin = _authenticate(pluginInstance, caller); } catch { plugin = null; }
        if (_disposed) return new BarProviderResult(BarStatus.Unavailable, null);
        if (plugin == null) return new BarProviderResult(BarStatus.UnknownPlugin, null);
        if (!ReferenceEquals(plugin.Assembly, caller)) return new BarProviderResult(BarStatus.CallerMismatch, null);
        string segment = StoryProviderIdentity.Segment(plugin);
        var binding = _bindings.Bind(segment, plugin.PluginId);
        if (binding == StoryBindingStatus.Conflict) return new BarProviderResult(BarStatus.ProviderConflict, null);
        if (binding == StoryBindingStatus.LimitExceeded) return new BarProviderResult(BarStatus.LimitExceeded, null);
        if (_leases.ContainsKey(segment)) return new BarProviderResult(BarStatus.AlreadyAcquired, null);
        var lease = new Lease(this, segment, plugin.PluginId);
        _leases.Add(segment, lease);
        return new BarProviderResult(BarStatus.Succeeded, lease);
    }

    private bool Active(Lease lease) => !_disposed && _leases.TryGetValue(lease.ProviderId, out var current) && ReferenceEquals(current, lease);
    private BarResult? Guard(Lease lease, Guid session)
    {
        _checkThread();
        if (!Active(lease)) return new BarResult(BarStatus.Unavailable);
        if (_lifecycle.CurrentSession?.Id != session) return new BarResult(BarStatus.StaleSession);
        if (!_persistence.CanMutate(session)) return new BarResult(BarStatus.Unavailable);
        return null;
    }
    private void OnLifecycle(LifecycleEvent value)
    {
        if (_lifecycle.CurrentSession?.Id != value.Session?.Id || _lifecycle.CurrentSession?.Phase != value.Session?.Phase) return;
        if (value.Kind == LifecycleEventKind.SessionStarting || value.Kind == LifecycleEventKind.SessionInvalidated || value.Kind == LifecycleEventKind.SessionStartFailed)
            _transient.Clear();
    }
    public void Dispose()
    {
        _checkThread();
        if (_disposed) return;
        _disposed = true;
        foreach (var lease in _leases.Values) { lease.Definitions.Clear(); lease.Stations.Clear(); }
        _leases.Clear(); _transient.Clear();
        _subscription.Dispose(); _persistence.Dispose();
    }
}
