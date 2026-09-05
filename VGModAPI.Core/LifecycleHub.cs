using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VGModAPI.Core;

internal sealed class LifecycleHub : ILifecycleApi, ILifecycleDispatchState, IDisposable
{
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly Action<string, Exception> _report;
    private readonly List<Subscription> _subscriptions = new();
    private readonly Queue<LifecycleEvent> _pending = new();
    private readonly Dictionary<string, CapabilityStatus> _capabilities = new();
    private SessionSnapshot? _session;
    private bool _dispatching;
    private bool _disposed;

    internal LifecycleHub(Action<string, Exception> report) => _report = report;
    public bool IsDispatchingCallbacks { get { CheckThread(); return _dispatching; } }
    public SessionSnapshot? CurrentSession { get { CheckThread(); return _session; } }
    public IReadOnlyList<CapabilityStatus> Capabilities
    { get { CheckThread(); return Array.AsReadOnly(_capabilities.Values.OrderBy(c => c.Name).ToArray()); } }

    internal void SetCapability(string name, bool available, string detail)
    {
        CheckThread();
        _capabilities[name] = new CapabilityStatus(name, available, false, detail);
    }

    public IDisposable Subscribe(string owner, Action<LifecycleEvent> callback)
    {
        CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(LifecycleHub));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("An owner ID is required.", nameof(owner));
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        var sub = new Subscription(this, owner, callback);
        _subscriptions.Add(sub);
        return sub;
    }

    internal Guid Begin(SessionOrigin origin, string? path)
    {
        CheckThread();
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
        if (_disposed) return;
        _pending.Enqueue(message);
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            while (_pending.Count > 0 && !_disposed)
            {
                var next = _pending.Dequeue();
                foreach (var sub in _subscriptions.ToArray())
                {
                    if (!sub.Active) continue;
                    try { sub.Callback(next); }
                    catch (Exception ex)
                    {
                        try { _report(sub.Owner, ex); } catch { /* Diagnostics must not break dispatch. */ }
                    }
                }
            }
        }
        finally { _dispatching = false; }
    }

    internal void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread)
            throw new InvalidOperationException("VGModAPI lifecycle access requires the Unity main thread.");
    }

    public void Dispose()
    {
        CheckThread();
        _disposed = true;
        foreach (var sub in _subscriptions) sub.Active = false;
        _subscriptions.Clear();
        _pending.Clear();
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
