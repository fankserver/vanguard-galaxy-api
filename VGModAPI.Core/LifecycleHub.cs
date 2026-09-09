using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VGModAPI.Core;

internal sealed class LifecycleHub : ILifecycleService, IDisposable
{
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly Action<string, Exception> _report;
    private readonly List<Subscription> _subscriptions = new();
    private readonly Queue<LifecycleEvent> _pending = new();
    internal ServiceStatusRegistry Services { get; }
    private readonly ServiceSubscriptions<LifecycleEvent> _events;
    private readonly IServiceStatus _sessionTracking, _saveOutcomes;
    private int _serviceDispatchDepth;
    private SessionSnapshot? _session;
    private bool _dispatching;
    private bool _disposed;
    private bool _disposeRequested;

    internal LifecycleHub(Action<string, Exception> report)
    {
        _report = report;
        Services = new ServiceStatusRegistry(CheckThread, report, EnterServiceDispatch);
        _sessionTracking = Services.Get("session-lifecycle");
        _saveOutcomes = Services.Get("save-outcomes");
        _events = new ServiceSubscriptions<LifecycleEvent>(this, Subscribe,
            fact => fact.Kind == LifecycleEventKind.SessionInvalidated ||
                (fact.Kind >= LifecycleEventKind.SaveStarted ? SaveOutcomes : SessionTracking).Availability.IsAvailable);
    }
    public IServiceStatus SessionTracking { get { CheckThread(); return _sessionTracking; } }
    public IServiceStatus SaveOutcomes { get { CheckThread(); return _saveOutcomes; } }
    SessionSnapshot? ILifecycleService.CurrentSession => SessionTracking.Availability.IsAvailable ||
        CurrentSession?.Phase == SessionPhase.Invalidated ? CurrentSession : null;
    public event Action<LifecycleEvent>? Changed { add => _events.Add(value); remove => _events.Remove(value); }
    public bool IsDispatchingCallbacks { get { CheckThread(); return _dispatching || _serviceDispatchDepth != 0; } }
    public SessionSnapshot? CurrentSession { get { CheckThread(); return _session; } }
    public IReadOnlyList<CapabilityStatus> Capabilities => Services.Untyped;

    internal void SetCapability(string name, bool available, string detail,
        ServiceUnavailableReason reason = ServiceUnavailableReason.BindingFailed)
        => Services.Set(name, available, detail, reason);

    internal IDisposable EnterServiceDispatch()
    {
        CheckThread();
        ++_serviceDispatchDepth;
        return new DispatchScope(this);
    }

    private sealed class DispatchScope : IDisposable
    {
        private readonly LifecycleHub _hub;
        private bool _disposed;
        internal DispatchScope(LifecycleHub hub) { _hub = hub; }
        public void Dispose()
        {
            _hub.CheckThread();
            if (_disposed) return;
            _disposed = true;
            --_hub._serviceDispatchDepth;
        }
    }

    internal IDisposable Subscribe(string owner, Action<LifecycleEvent> callback)
    {
        CheckThread();
        if (_disposed || Services.IsStopping) throw new ObjectDisposedException(nameof(LifecycleHub));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("An owner ID is required.", nameof(owner));
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        var sub = new Subscription(this, owner, callback);
        _subscriptions.Add(sub);
        return sub;
    }

    internal Guid Begin(SessionOrigin origin, string? path)
    {
        CheckThread();
        if (Services.IsStopping) throw new ObjectDisposedException(nameof(LifecycleHub));
        // Install the new snapshot before delivering either event: reentrant game actions
        // must never be overwritten by the remainder of this operation.
        var previous = _session;
        var next = new SessionSnapshot(Guid.NewGuid(), SessionPhase.Starting, origin, path);
        _session = next;
        if (previous != null && previous.Phase != SessionPhase.Invalidated)
            _pending.Enqueue(new LifecycleEvent(LifecycleEventKind.SessionInvalidated,
                new SessionSnapshot(previous.Id, SessionPhase.Invalidated, previous.Origin, previous.SavePath), detail: "Replaced by another start attempt."));
        Publish(new LifecycleEvent(LifecycleEventKind.SessionStarting, next));
        return next.Id;
    }

    internal void PlayerReady(Guid id) => Transition(id, SessionPhase.Starting, SessionPhase.PlayerReady, LifecycleEventKind.PlayerReady);
    internal void GameplayInitialized(Guid id) => Transition(id, SessionPhase.PlayerReady, SessionPhase.GameplayInitialized, LifecycleEventKind.GameplayInitialized);

    private void Transition(Guid id, SessionPhase from, SessionPhase to, LifecycleEventKind kind)
    {
        CheckThread();
        if (_session?.Id != id || _session.Phase != from) return;
        _session = new SessionSnapshot(id, to, _session.Origin, _session.SavePath);
        Publish(new LifecycleEvent(kind, _session));
    }

    internal void Fail(Guid id, string reason)
    {
        CheckThread();
        if (_session?.Id != id || (_session.Phase != SessionPhase.Starting && _session.Phase != SessionPhase.PlayerReady)) return;
        _session = new SessionSnapshot(id, SessionPhase.Failed, _session.Origin, _session.SavePath);
        Publish(new LifecycleEvent(LifecycleEventKind.SessionStartFailed, _session, detail: reason));
    }

    internal void Invalidate(string reason)
    {
        CheckThread();
        if (_session == null || _session.Phase == SessionPhase.Invalidated) return;
        _session = new SessionSnapshot(_session.Id, SessionPhase.Invalidated, _session.Origin, _session.SavePath);
        Publish(new LifecycleEvent(LifecycleEventKind.SessionInvalidated, _session, detail: reason));
    }

    internal void Publish(LifecycleEvent message)
    {
        CheckThread();
        if (_disposed || (Services.IsStopping && message.Kind != LifecycleEventKind.SessionInvalidated)) return;
        _pending.Enqueue(message);
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            while (_pending.Count > 0 && !_disposed)
            {
                var next = _pending.Dequeue();
                if (Services.IsStopping && next.Kind != LifecycleEventKind.SessionInvalidated) continue;
                foreach (var sub in _subscriptions.ToArray())
                {
                    if (Services.IsStopping && next.Kind != LifecycleEventKind.SessionInvalidated) break;
                    if (!sub.Active) continue;
                    try { sub.Callback(next); }
                    catch (Exception ex)
                    {
                        try { _report(sub.Owner, ex); } catch { /* Diagnostics must not break dispatch. */ }
                    }
                }
            }
        }
        finally
        {
            _dispatching = false;
            if (_disposeRequested) FinishDispose();
        }
    }

    internal void ReportSubscriberFailure(string owner, Exception error)
    {
        try { _report(owner, error); } catch { /* Diagnostics must not interrupt observers. */ }
    }

    internal void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread)
            throw new InvalidOperationException("VGModAPI lifecycle access requires the Unity main thread.");
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed || _disposeRequested) return;
        _disposeRequested = true;
        Services.BeginStop();
        Invalidate("API shutting down.");
        if (!_dispatching) FinishDispose();
    }

    private void FinishDispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var sub in _subscriptions) sub.Active = false;
        _subscriptions.Clear();
        _pending.Clear();
        _events.Dispose();
        Services.Dispose();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly LifecycleHub _hub;
        internal readonly string Owner;
        internal readonly Action<LifecycleEvent> Callback;
        internal bool Active = true;
        internal Subscription(LifecycleHub hub, string owner, Action<LifecycleEvent> callback)
        { _hub = hub; Owner = owner; Callback = callback; }
        public void Dispose()
        {
            _hub.CheckThread();
            Active = false;
            _hub._subscriptions.Remove(this);
        }
    }
}
