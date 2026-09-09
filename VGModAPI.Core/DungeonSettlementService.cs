using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class DungeonSettlementService : IDungeonSettlement, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IDisposable _boardingSubscription, _lifetime;
    private readonly Dictionary<BoardingHandle, DungeonSettlementSnapshot> _states = new();
    private readonly List<Subscription> _subscribers = new();
    private readonly Action<string, Exception> _report;
    private bool _disposed;
    private int _dispatchDepth;
    public bool IsDispatchingCallbacks { get { _hub.CheckThread(); return _dispatchDepth != 0; } }
    internal DungeonSettlementService(LifecycleHub hub, BoardingService boarding, Action<string, Exception> report)
    {
        _hub = hub; _report = report;
        _boardingSubscription = boarding.Subscribe("vgmodapi.settlement", Observe);
        _lifetime = hub.Subscribe("vgmodapi.settlement", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed) _states.Clear();
        });
    }
    public DungeonSettlementSnapshot? Get(BoardingHandle operation)
    { _hub.CheckThread(); return !_disposed && operation.SessionId == _hub.CurrentSession?.Id && _states.TryGetValue(operation, out var state) ? state : null; }
    public IDisposable Subscribe(string pluginId, Action<DungeonSettlementSnapshot> callback)
    {
        _hub.CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(DungeonSettlementService));
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("Plugin ID required.");
        var subscription = new Subscription(this, pluginId, callback ?? throw new ArgumentNullException(nameof(callback))); _subscribers.Add(subscription); return subscription;
    }
    private void Observe(BoardingEvent message)
    {
        if (_disposed || message.Operation == null || message.Operation.Handle.SessionId != _hub.CurrentSession?.Id) return;
        var handle = message.Operation.Handle;
        if (message.Kind == BoardingEventKind.OperationRetired) { _states.Remove(handle); return; }
        var previous = Get(handle);
        var snapshot = new DungeonSettlementSnapshot(handle, message.Operation.Outcome ?? "Unknown",
            previous?.CaptureApplied == true || message.Kind == BoardingEventKind.CaptureApplied,
            previous?.CrewReturnSettled == true || message.Kind == BoardingEventKind.CrewReturnSettled,
            previous?.Casualties ?? new Dictionary<string, int>(), previous?.PrisonersDelivered ?? new Dictionary<string, int>(), previous?.CrewCountsObserved ?? false);
        Publish(snapshot);
    }
    internal void ObserveCrew(BoardingHandle operation, IReadOnlyDictionary<string, int> casualties, IReadOnlyDictionary<string, int> prisonersDelivered)
    {
        _hub.CheckThread(); var previous = Get(operation); if (previous == null) return;
        if (previous.CrewCountsObserved && Same(previous.Casualties, casualties) && Same(previous.PrisonersDelivered, prisonersDelivered)) return;
        Publish(new(operation, previous.NativeOutcome, previous.CaptureApplied, previous.CrewReturnSettled, casualties, prisonersDelivered, true));
    }
    private static bool Same(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private void Publish(DungeonSettlementSnapshot state)
    {
        _states[state.Operation] = state;
        _dispatchDepth++;
        try
        {
            foreach (var subscriber in _subscribers.ToArray())
            {
                if (_disposed || state.Operation.SessionId != _hub.CurrentSession?.Id) return;
                if (!subscriber.Active) continue;
                try { subscriber.Callback(state); } catch (Exception error) { try { _report(subscriber.Id, error); } catch { } }
            }
        }
        finally { _dispatchDepth--; }
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return; _disposed = true;
        _boardingSubscription.Dispose(); _lifetime.Dispose(); _states.Clear();
        foreach (var subscriber in _subscribers.ToArray()) subscriber.Dispose();
    }
    private sealed class Subscription : IDisposable
    {
        private readonly DungeonSettlementService _owner;
        internal readonly string Id;
        internal readonly Action<DungeonSettlementSnapshot> Callback;
        internal bool Active = true;
        internal Subscription(DungeonSettlementService owner, string id, Action<DungeonSettlementSnapshot> callback) { _owner = owner; Id = id; Callback = callback; }
        public void Dispose() { _owner._hub.CheckThread(); Active = false; _owner._subscribers.Remove(this); }
    }
}
