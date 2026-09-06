using System;
using System.Collections.Generic;
using System.Threading;

namespace VGModAPI.Core;

// Main-thread, isolated-subscriber, reentrancy-safe dispatch hub for station-lifetime
// facts. Mirrors TravelEvents safety rather than composing it, because station facts are
// deliberately distinct from travel legs and placements.
internal sealed class StationEvents : IStationEvents, IDisposable
{
    private sealed class Subscription : IDisposable
    {
        internal readonly string Owner;
        internal readonly Action<StationTransition> Callback;
        internal bool Active = true;
        private readonly StationEvents _hub;
        internal Subscription(StationEvents hub, string owner, Action<StationTransition> callback) { _hub = hub; Owner = owner; Callback = callback; }
        public void Dispose() { _hub.CheckThread(); Active = false; _hub._subscriptions.Remove(this); }
    }
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly Action<string, Exception> _report;
    private readonly List<Subscription> _subscriptions = new();
    private readonly Queue<(long Epoch, StationTransition Event)> _queue = new();
    private Guid? _session;
    private long _epoch, _sequence;
    private bool _dispatching, _disposed;
    internal StationEvents(Action<string, Exception> report) { _report = report; }
    public Guid? SessionId { get { CheckThread(); return _session; } }
    public bool IsDispatchingCallbacks { get { CheckThread(); return _dispatching; } }
    public IDisposable Subscribe(string owner, Action<StationTransition> callback)
    {
        CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(StationEvents));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Owner identity required.", nameof(owner));
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        var subscription = new Subscription(this, owner, callback); _subscriptions.Add(subscription); return subscription;
    }
    internal void SetSession(Guid? session)
    {
        CheckThread();
        if (session == Guid.Empty) throw new ArgumentException("Empty session identity.", nameof(session));
        if (_disposed || _session == session) return;
        _session = session; _epoch++; _sequence = 0; _queue.Clear();
    }
    internal long Emit(Guid session, StationTransitionKind kind, TravelLocation? station, double now)
    {
        CheckThread();
        if (_disposed || _session != session) return 0;
        var fact = new StationTransition(session, _sequence + 1, kind, station, now);
        _sequence++;
        _queue.Enqueue((_epoch, fact));
        Dispatch();
        return fact.Sequence;
    }
    private void Dispatch()
    {
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
        CheckThread(); _disposed = true; _session = null; _queue.Clear();
        foreach (var subscription in _subscriptions) subscription.Active = false;
        _subscriptions.Clear();
    }
    private void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("Station API access is main-thread-only.");
    }
}
