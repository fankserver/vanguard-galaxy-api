using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>One source for binding facts and effective typed health. Session/provider readiness is separate.</summary>
internal sealed class ServiceStatusRegistry : IDisposable
{
    private sealed class Source
    {
        internal readonly CapabilityStatus Untyped;
        internal readonly ServiceAvailability Health;
        internal Source(string name, bool available, string detail, ServiceUnavailableReason reason)
        {
            Untyped = new CapabilityStatus(name, available, detail);
            Health = available ? ServiceAvailability.Available : new ServiceAvailability(reason, detail);
        }
    }

    private sealed class View : IServiceStatus, IDisposable
    {
        private readonly ServiceStatusRegistry _registry;
        internal readonly string Name;
        private readonly ServiceNotifications<ServiceAvailability> _events;
        internal ServiceAvailability Published;
        internal View(ServiceStatusRegistry registry, string name)
        {
            _registry = registry; Name = name; Published = registry.Read(name);
            _events = new ServiceNotifications<ServiceAvailability>(registry._checkThread, registry._report, registry._enterDispatch);
        }
        public ServiceAvailability Availability { get { _registry._checkThread(); return _registry.Read(Name); } }
        public event Action<ServiceAvailability>? AvailabilityChanged
        {
            add
            {
                _registry._checkThread();
                if (_registry._stopped) throw new ObjectDisposedException(nameof(ServiceStatusRegistry));
                _events.Add(value);
            }
            remove => _events.Remove(value);
        }
        internal void Notify(ServiceAvailability state) => _events.Publish(state);
        public void Dispose() => _events.Dispose();
    }

    private readonly Action _checkThread;
    private readonly Action<string, Exception> _report;
    private readonly Func<IDisposable> _enterDispatch;
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, View> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<bool>> _faults = new(StringComparer.Ordinal);
    private readonly Queue<(View View, ServiceAvailability State)> _pending = new();
    private bool _notifying, _stopped, _disposed, _disposeRequested;
    private readonly List<Action> _cleanup = new();

    internal void AfterStopped(Action cleanup)
    {
        _checkThread();
        if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));
        if (_disposed) cleanup(); else _cleanup.Add(cleanup);
    }
    private readonly Dictionary<string, string[]> _dependencies = new(StringComparer.Ordinal)
    {
        ["dungeon-settlement"] = new[] { "dungeon-rewards", "boarding-observation" },
        ["save-data"] = new[] { "session-lifecycle", "save-outcomes" },
        ["mission-transitions"] = new[] { "session-lifecycle" },
        ["mission-continuity"] = new[] { "mission-transitions", "save-data" },
        ["native-travel"] = new[] { "session-lifecycle" },
        ["gameplay-ui"] = new[] { "session-lifecycle" }
    };

    internal ServiceStatusRegistry(Action checkThread, Action<string, Exception> report, Func<IDisposable> enterDispatch)
    { _checkThread = checkThread; _report = report; _enterDispatch = enterDispatch; }

    internal IServiceStatus Get(string name)
    {
        _checkThread();
        if (_stopped) throw new ObjectDisposedException(nameof(ServiceStatusRegistry));
        if (!_views.TryGetValue(name, out var view)) _views.Add(name, view = new View(this, name));
        return view;
    }

    internal IReadOnlyList<CapabilityStatus> Untyped
    {
        get
        {
            _checkThread();
            return Array.AsReadOnly(_sources.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            {
                var health = Read(pair.Key);
                return health.IsAvailable ? pair.Value.Untyped : new CapabilityStatus(pair.Key, false, health.Detail);
            }).ToArray());
        }
    }

    internal void Set(string name, bool available, string detail, ServiceUnavailableReason reason)
    {
        _checkThread();
        if (_stopped) return;
        if (!available && reason == ServiceUnavailableReason.None) throw new ArgumentException("An unavailable feature requires a reason.", nameof(reason));
        _sources[name] = new Source(name, available, detail, reason);
        Refresh();
    }

    /// <summary>Internal volatile fault latches only. Reading a status never pumps callbacks or game work.</summary>
    internal void WatchFault(string name, Func<bool> faulted)
    {
        _checkThread();
        if (_stopped) throw new ObjectDisposedException(nameof(ServiceStatusRegistry));
        _faults[name] = faulted ?? throw new ArgumentNullException(nameof(faulted));
        Refresh();
    }

    private ServiceAvailability Read(string name)
    {
        if (_stopped) return new ServiceAvailability(ServiceUnavailableReason.ApiStopped, "API stopped.");
        if (_faults.TryGetValue(name, out var faulted) && faulted())
            return new ServiceAvailability(ServiceUnavailableReason.ObserverFault, "Observer fault; restart required.");
        if (!_sources.TryGetValue(name, out var source)) return new ServiceAvailability(ServiceUnavailableReason.BindingFailed, "Not initialized.");
        if (!source.Health.IsAvailable) return source.Health;
        if (_dependencies.TryGetValue(name, out var dependencies))
            foreach (var dependency in dependencies)
            {
                var health = Read(dependency);
                if (!health.IsAvailable)
                    return new ServiceAvailability(ServiceUnavailableReason.DependencyUnavailable, dependency + ": " + health.Detail);
            }
        return source.Health;
    }

    internal void Refresh()
    {
        _checkThread();
        if (_disposed) return;
        // Commit the complete affected view before any subscriber is called.
        foreach (var view in _views.Values.ToArray())
        {
            var current = Read(view.Name);
            if (current.Equals(view.Published)) continue;
            view.Published = current;
            _pending.Enqueue((view, current));
        }
        if (_notifying) return;
        _notifying = true;
        try
        {
            using var scope = _enterDispatch();
            while (_pending.Count != 0 && !_disposed)
            {
                var next = _pending.Dequeue();
                next.View.Notify(next.State);
            }
        }
        finally
        {
            _notifying = false;
            if (_disposeRequested) FinishDispose();
        }
    }

    internal bool IsStopping => _stopped;

    /// <summary>Close gates first. The caller invalidates session state before publishing stopped notifications.</summary>
    internal void BeginStop() { _checkThread(); _stopped = true; }

    public void Dispose()
    {
        _checkThread();
        if (_disposed || _disposeRequested) return;
        _disposeRequested = true;
        BeginStop(); Refresh();
        if (!_notifying) FinishDispose();
    }

    private void FinishDispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var view in _views.Values) view.Dispose();
        _pending.Clear(); _faults.Clear();
        foreach (var cleanup in _cleanup.ToArray())
        {
            try { cleanup(); }
            catch (Exception error) { try { _report("service cleanup", error); } catch { } }
        }
        _cleanup.Clear();
    }
}
