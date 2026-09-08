using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Observation registry; the adapter supplies copied facts, never native objects.</summary>
internal sealed class BoardingService : IBoardingEvents, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IDisposable _lifecycle;
    private readonly Action<string, Exception> _report;
    private readonly Dictionary<BoardingHandle, BoardingTargetSnapshot> _targets = new();
    private readonly Dictionary<BoardingHandle, BoardingOperationSnapshot> _operations = new();
    private readonly List<Subscription> _subscribers = new();
    private readonly HashSet<BoardingHandle> _retired = new();
    private readonly HashSet<BoardingHandle> _retiredOperations = new();
    private readonly Queue<BoardingEvent> _pending = new();
    private Guid? _session;
    private long _sequence;
    private bool _dispatching, _disposed;
    internal BoardingService(LifecycleHub hub, Action<string, Exception> report)
    {
        _hub = hub; _report = report;
        _lifecycle = hub.Subscribe("vgmodapi.boarding", OnLifecycle);
        var session = hub.CurrentSession;
        if (session?.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized) _session = session.Id;
    }
    public Guid? SessionId { get { _hub.CheckThread(); return _session; } }
    public bool IsDispatchingCallbacks { get { _hub.CheckThread(); return _dispatching; } }
    public IReadOnlyList<BoardingTargetSnapshot> GetTargets() { _hub.CheckThread(); return Array.AsReadOnly(_targets.Values.ToArray()); }
    public IReadOnlyList<BoardingOperationSnapshot> GetOperations() { _hub.CheckThread(); return Array.AsReadOnly(_operations.Values.ToArray()); }
    public BoardingTargetSnapshot? GetTarget(BoardingHandle handle) { _hub.CheckThread(); return _targets.TryGetValue(handle, out var value) ? value : null; }
    public BoardingOperationSnapshot? GetOperation(BoardingHandle handle) { _hub.CheckThread(); return _operations.TryGetValue(handle, out var value) ? value : null; }
    private void OnLifecycle(LifecycleEvent message)
    {
        if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            Invalidate();
        if (message.Kind == LifecycleEventKind.PlayerReady && _hub.CurrentSession?.Id == message.Session?.Id)
        { _session = message.Session!.Id; _sequence = 0; }
    }
    internal void Invalidate()
    {
        _hub.CheckThread(); _session = null; _targets.Clear(); _operations.Clear(); _pending.Clear(); _retired.Clear(); _retiredOperations.Clear();
    }
    internal bool Observe(BoardingEventKind kind, BoardingTargetSnapshot target, BoardingOperationSnapshot? operation = null, BoardingDelivery? delivery = null)
    {
        _hub.CheckThread();
        if (_disposed || !_session.HasValue || target.Handle.SessionId != _session) return false;
        var targetRetired = _retired.Contains(target.Handle);
        if (targetRetired && (operation == null || !_operations.ContainsKey(operation.Handle) || kind is BoardingEventKind.Retired or BoardingEventKind.TargetAvailable or BoardingEventKind.TargetChanged)) return false;
        if (operation != null && _retiredOperations.Contains(operation.Handle)) return false;
        if (operation != null && !operation.Target.Equals(target.Handle)) throw new ArgumentException("Operation target mismatch.");
        if (_targets.TryGetValue(target.Handle, out var oldTarget) && target.Revision < oldTarget.Revision) return false;
        if (operation != null && _operations.TryGetValue(operation.Handle, out var oldOperation) && operation.Revision < oldOperation.Revision) return false;
        if (kind == BoardingEventKind.Retired)
        {
            _retired.Add(target.Handle); _targets.Remove(target.Handle);
        }
        else if (kind == BoardingEventKind.OperationRetired)
        {
            if (operation == null) throw new ArgumentException("Operation retirement requires an operation.");
            _operations.Remove(operation.Handle); _retiredOperations.Add(operation.Handle);
            if (!targetRetired) _targets[target.Handle] = target;
        }
        else
        {
            if (!targetRetired) _targets[target.Handle] = target;
            if (operation != null) _operations[operation.Handle] = operation;
        }
        _pending.Enqueue(new BoardingEvent(++_sequence, kind, target, operation, delivery));
        Dispatch(); return true;
    }
    private void Dispatch()
    {
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            while (_pending.Count != 0 && !_disposed)
            {
                var message = _pending.Dequeue();
                foreach (var sub in _subscribers.ToArray())
                {
                    if (_session != message.Target.Handle.SessionId || _disposed) break;
                    if (!sub.Active) continue;
                    try { sub.Callback(message); }
                    catch (Exception error) { try { _report(sub.Provider, error); } catch { } }
                }
            }
        }
        finally { _dispatching = false; }
    }
    public IDisposable Subscribe(string providerId, Action<BoardingEvent> callback)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(BoardingService));
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("Provider ID required.", nameof(providerId));
        var sub = new Subscription(this, providerId, callback ?? throw new ArgumentNullException(nameof(callback)));
        _subscribers.Add(sub); return sub;
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; Invalidate(); _lifecycle.Dispose();
        foreach (var sub in _subscribers) sub.Active = false;
        _subscribers.Clear();
    }
    private sealed class Subscription : IDisposable
    {
        private readonly BoardingService _owner;
        internal readonly string Provider;
        internal readonly Action<BoardingEvent> Callback;
        internal bool Active = true;
        internal Subscription(BoardingService owner, string provider, Action<BoardingEvent> callback) { _owner = owner; Provider = provider; Callback = callback; }
        public void Dispose() { _owner._hub.CheckThread(); Active = false; _owner._subscribers.Remove(this); }
    }
}
