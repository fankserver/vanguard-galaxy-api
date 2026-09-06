using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VGModAPI.Core;

internal sealed class TravelEvents : ITravelEvents, IDisposable
{
    private sealed class Subscription : IDisposable
    {
        internal readonly string Owner;
        internal readonly Action<TravelTransition> Callback;
        internal bool Active = true;
        private readonly TravelEvents _hub;
        internal Subscription(TravelEvents hub, string owner, Action<TravelTransition> callback) { _hub = hub; Owner = owner; Callback = callback; }
        public void Dispose() { _hub.CheckThread(); Active = false; _hub._subscriptions.Remove(this); }
    }
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly Action<string, Exception> _report;
    private readonly List<Subscription> _subscriptions = new();
    private readonly Queue<(long Epoch, TravelTransition Event)> _queue = new();
    private Guid? _session;
    private TravelLocation? _location;
    private long _epoch, _sequence;
    private bool _dispatching, _disposed;
    internal TravelEvents(Action<string, Exception> report) { _report = report; }
    public Guid? SessionId { get { CheckThread(); return _session; } }
    public TravelLocation? CurrentLocation { get { CheckThread(); return _location; } }
    public bool IsDispatchingCallbacks { get { CheckThread(); return _dispatching; } }
    public IDisposable Subscribe(string owner, Action<TravelTransition> callback)
    {
        CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(TravelEvents));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Owner identity required.", nameof(owner));
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        var subscription = new Subscription(this, owner, callback); _subscriptions.Add(subscription); return subscription;
    }
    internal void SetSession(Guid? session)
    {
        CheckThread();
        if (session == Guid.Empty) throw new ArgumentException("Empty session identity.", nameof(session));
        if (_disposed || _session == session) return;
        _session = session; _location = null; _epoch++; _sequence = 0; _queue.Clear();
    }
    internal void Emit(Guid session, Guid? operation, TravelTransitionKind kind, TravelMode mode,
        TravelLocation? origin, TravelLocation? requested, TravelLocation? actual, double now, double? dwell = null)
    {
        CheckThread();
        if (_disposed || _session != session) return;
        var fact = new TravelTransition(session, operation, _sequence + 1, kind, mode, origin, requested, actual, now, dwell);
        _sequence++;
        if (kind == TravelTransitionKind.Departed) _location = null;
        if (kind is TravelTransitionKind.InitialPlacement or TravelTransitionKind.RecoveredPlacement or TravelTransitionKind.Arrived) _location = actual;
        _queue.Enqueue((_epoch, fact));
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            while (_queue.Count > 0 && !_disposed)
            {
                var item = _queue.Dequeue();
                if (item.Epoch != _epoch) continue;
                foreach (var subscription in _subscriptions.ToArray())
                {
                    if (_disposed || item.Epoch != _epoch) break;
                    if (!subscription.Active) continue;
                    try { subscription.Callback(item.Event); }
                    catch (Exception error) { try { _report(subscription.Owner, error); } catch { } }
                }
            }
        }
        finally { _dispatching = false; }
    }
    public void Dispose()
    {
        CheckThread(); _disposed = true; _session = null; _location = null; _queue.Clear();
        foreach (var subscription in _subscriptions) subscription.Active = false;
        _subscriptions.Clear();
    }
    private void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("Travel API access is main-thread-only.");
    }
}
