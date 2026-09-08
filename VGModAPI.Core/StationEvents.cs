using System;
using System.Collections.Generic;
using System.Threading;

namespace VGModAPI.Core;

// Main-thread, isolated-subscriber, reentrancy-safe dispatch hub for station-lifetime
// facts. Mirrors TravelEvents safety rather than composing it, because station facts are
// deliberately distinct from travel legs and placements.
internal sealed class StationEvents : IStationService, IDisposable
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
    private readonly LifecycleHub _lifecycle;
    private readonly IServiceStatus _status;
    private readonly ServiceSubscriptions<StationTransition> _events;
    private readonly List<Subscription> _subscriptions = new();
    private readonly Queue<(long Epoch, StationTransition Event)> _queue = new();
    private Guid? _session;
    private long _epoch, _sequence;
    private bool _dispatching, _disposed;
    internal StationEvents(LifecycleHub lifecycle, Action<string, Exception>? report = null)
    {
        _lifecycle = lifecycle; _report = report ?? lifecycle.ReportSubscriberFailure;
        _status = lifecycle.Services.Get("native-travel");
        _events = new ServiceSubscriptions<StationTransition>(lifecycle, Subscribe,
            fact => InSession(fact.SessionId));
    }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public event Action<StationTransition>? Transitioned { add => _events.Add(value); remove => _events.Remove(value); }
    Guid? IStationService.SessionId
    { get { CheckThread(); return _session.HasValue && InSession(_session.Value) ? _session : null; } }
    private bool InSession(Guid id)
    {
        CheckThread();
        return !_disposed && Availability.IsAvailable && _lifecycle.CurrentSession?.Id == id &&
            _lifecycle.CurrentSession.Phase is not (SessionPhase.Failed or SessionPhase.Invalidated);
    }
    public Guid? SessionId { get { CheckThread(); return _session; } }
    public bool IsDispatchingCallbacks { get { CheckThread(); return _dispatching || _lifecycle.IsDispatchingCallbacks; } }
    internal IDisposable Subscribe(string owner, Action<StationTransition> callback)
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
        CheckThread(); if (_disposed) return;
        _disposed = true; _session = null; _queue.Clear(); _events.Dispose();
        foreach (var subscription in _subscriptions) subscription.Active = false;
        _subscriptions.Clear();
        if (Availability.IsAvailable)
            _lifecycle.SetCapability("native-travel", false, "Station service stopped.", ServiceUnavailableReason.ApiStopped);
    }
    private void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("Station API access is main-thread-only.");
    }
}
