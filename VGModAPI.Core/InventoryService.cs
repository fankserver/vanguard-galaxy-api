using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal interface IInventoryBackend
{
    InventorySnapshotSet Discover(Guid session);
    InventorySnapshot? Resolve(Guid session, InventoryReference reference);
    PreparedInventoryMove Prepare(InventoryHandle source, InventoryHandle destination, Guid stack, int quantity, InventoryTransferOptions options);
}
internal sealed class PreparedInventoryMove
{
    internal readonly InventoryTransferStatus Status;
    internal readonly int Quantity;
    internal readonly InventoryPairCommit? Commit;
    internal readonly Action Refresh;
    internal PreparedInventoryMove(InventoryTransferStatus status, int quantity = 0, InventoryPairCommit? commit = null, Action? refresh = null)
    { Status = status; Quantity = quantity; Commit = commit; Refresh = refresh ?? (() => { }); }
}
internal sealed partial class InventoryService : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly Func<IInventoryBackend?> _backend;
    private readonly IDisposable _lifetime;
    private readonly Dictionary<Guid, Entry> _operations = new();
    private readonly Dictionary<Guid, (string Owner, Action<InventoryTransferResult> Callback)> _listeners = new();
    private Entry? _pending;
    private bool _busy, _disposed;
    private int _saveDepth, _serializationDepth;
    internal InventoryService(LifecycleHub hub, Func<IInventoryBackend?> backend)
    {
        _hub = hub; _backend = backend; _status = hub.Services.Get("inventories");
        _lifetime = hub.Subscribe("vgmodapi.inventories", e =>
        {
            if (e.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            {
                _operations.Clear();
                foreach (var move in _moves.ToArray())
                    if (!move.Game.IsActive) { move.End(InventoryTransferStatus.GameEnded); _moves.Remove(move); }
            }
            else if (e.Kind == LifecycleEventKind.SaveStarted) _saveDepth++;
            else if (e.Kind is LifecycleEventKind.SaveSucceeded or LifecycleEventKind.SaveFailed or LifecycleEventKind.SaveSkipped)
            { if (_saveDepth > 0) _saveDepth--; }
        });
    }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public Guid? SessionId { get { _hub.CheckThread(); return _hub.CurrentSession?.Id; } }
    private bool Ready(Guid session) => !_disposed && Availability.IsAvailable && session != Guid.Empty && SessionId == session && _hub.CurrentSession!.Phase == SessionPhase.GameplayInitialized;
    internal void BeginSerialization() { _hub.CheckThread(); AssertSafeToSave(); _serializationDepth++; }
    internal void EndSerialization() { _hub.CheckThread(); if (_serializationDepth > 0) _serializationDepth--; }
    internal void AssertSafeToSave()
    { if (_pending != null) throw new InvalidOperationException("Inventory recovery must finish before saving."); }
    public InventorySnapshotSet Discover(Guid expectedSessionId)
    {
        _hub.CheckThread();
        if (!Ready(expectedSessionId)) return new(InventoryTransferStatus.NotReady, Array.Empty<InventorySnapshot>());
        try { return _backend()?.Discover(expectedSessionId) ?? new(InventoryTransferStatus.NotReady, Array.Empty<InventorySnapshot>()); }
        catch (Exception error) { Report(error); return new(InventoryTransferStatus.Failed, Array.Empty<InventorySnapshot>()); }
    }
    public InventorySnapshot? Resolve(Guid expectedSessionId, InventoryReference reference)
    {
        _hub.CheckThread(); if (reference == null) throw new ArgumentNullException(nameof(reference));
        if (!Ready(expectedSessionId)) return null;
        try { return _backend()?.Resolve(expectedSessionId, reference); }
        catch (Exception error) { Report(error); return null; }
    }
    public InventoryTransferResult Transfer(Guid operationId, InventoryHandle source, InventoryHandle destination, Guid stackId, int quantity, InventoryTransferOptions options)
    {
        _hub.CheckThread();
        InventoryTransferResult Refuse(InventoryTransferStatus status) => new(operationId, status, quantity, 0, 0, 0);
        if (source == null || destination == null || options == null || operationId == Guid.Empty || stackId == Guid.Empty || quantity < 1 || quantity > 100000 || source.Reference.Equals(destination.Reference))
            return Refuse(InventoryTransferStatus.InvalidRequest);
        if (source.SessionId != destination.SessionId || SessionId != source.SessionId) return Refuse(InventoryTransferStatus.GameEnded);
        if (_operations.TryGetValue(operationId, out var previous))
            return previous.Matches(source, destination, stackId, quantity, options) ? previous.Result : Refuse(InventoryTransferStatus.InvalidRequest);
        if (!Ready(source.SessionId) || _backend() == null) return Refuse(InventoryTransferStatus.NotReady);
        if (_pending != null) return Refuse(InventoryTransferStatus.Pending);
        if (_busy || _saveDepth > 0 || _serializationDepth > 0 || _hub.IsDispatchingCallbacks) return Refuse(InventoryTransferStatus.Pending);
        if (_operations.Count >= 4096) return Refuse(InventoryTransferStatus.LimitReached);
        var entry = new Entry(operationId, source, destination, stackId, quantity, options);
        _operations.Add(operationId, entry); _busy = true;
        try
        {
            var move = _backend()!.Prepare(source, destination, stackId, quantity, options); entry.Move = move;
            if (move.Commit == null) entry.Result = Refuse(move.Status);
            else if (!Ready(source.SessionId)) entry.Result = Refuse(InventoryTransferStatus.GameEnded);
            else
            {
                var status = move.Commit.Commit();
                if (status == InventoryCommitStatus.RecoveryRequired)
                { _pending = entry; entry.Result = new(operationId, InventoryTransferStatus.Pending, quantity, null, null, null); }
                else if (status == InventoryCommitStatus.Committed)
                { entry.Result = new(operationId, move.Quantity == quantity ? InventoryTransferStatus.Succeeded : InventoryTransferStatus.Partial, quantity, move.Quantity, move.Quantity, 0); Refresh(move); }
                else entry.Result = Refuse(InventoryTransferStatus.ItemChanged);
            }
        }
        catch (Exception error) { Report(error); entry.Result = Refuse(InventoryTransferStatus.Failed); }
        finally { _busy = false; }
        Notify(entry.Result); return entry.Result;
    }
    private InventoryTransferResult Recover(Guid expectedSessionId, Guid operationId)
    {
        _hub.CheckThread(); var entry = _pending;
        if (entry == null || entry.Id != operationId || entry.Source.SessionId != expectedSessionId)
            return new(operationId, InventoryTransferStatus.Missing, 0, 0, 0, 0);
        if (_busy || _hub.IsDispatchingCallbacks || _saveDepth > 0 || _serializationDepth > 0) return entry.Result;
        _busy = true;
        try
        {
            if (entry.Move!.Commit!.Recover() == InventoryCommitStatus.Unchanged)
            {
                _pending = null; entry.Result = new(operationId, InventoryTransferStatus.ItemChanged, entry.Quantity, 0, 0, 0);
                Refresh(entry.Move);
            }
        }
        finally { _busy = false; }
        if (_pending == null) Notify(entry.Result);
        return entry.Result;
    }
    private void Refresh(PreparedInventoryMove move) { try { move.Refresh(); } catch (Exception error) { Report(error); } }
    private void Report(Exception error) => _hub.ReportSubscriberFailure("inventories", error);
    private void Notify(InventoryTransferResult result)
    {
        _busy = true;
        try
        {
            using var dispatch = _hub.EnterServiceDispatch();
            foreach (var id in _listeners.Keys.ToArray())
                if (_listeners.TryGetValue(id, out var listener))
                    try { listener.Callback(result); } catch (Exception error) { _hub.ReportSubscriberFailure(listener.Owner, error); }
        }
        finally { _busy = false; }
    }
    public IDisposable Subscribe(string pluginId, Action<InventoryTransferResult> callback)
    {
        _hub.CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(InventoryService));
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > 512) throw new ArgumentException("Bounded plugin ID required.", nameof(pluginId));
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        var id = Guid.NewGuid(); _listeners.Add(id, (pluginId, callback)); return new Subscription(this, id);
    }
    public void Dispose()
    {
        _hub.CheckThread(); _disposed = true; _lifetime.Dispose(); _listeners.Clear(); _operations.Clear();
        foreach (var move in _moves) move.End(InventoryTransferStatus.GameEnded);
        _moves.Clear();
    }
    private sealed class Subscription : IDisposable
    {
        private readonly InventoryService _service; private readonly Guid _id;
        internal Subscription(InventoryService service, Guid id) { _service = service; _id = id; }
        public void Dispose() { _service._hub.CheckThread(); _service._listeners.Remove(_id); }
    }
    private sealed class Entry
    {
        internal readonly Guid Id, Stack; internal readonly InventoryHandle Source, Destination;
        internal readonly int Quantity; internal readonly InventoryTransferOptions Options;
        internal PreparedInventoryMove? Move; internal InventoryTransferResult Result;
        internal Entry(Guid id, InventoryHandle source, InventoryHandle destination, Guid stack, int quantity, InventoryTransferOptions options)
        { Id = id; Source = source; Destination = destination; Stack = stack; Quantity = quantity; Options = options; Result = new(id, InventoryTransferStatus.Pending, quantity, 0, 0, 0); }
        internal bool Matches(InventoryHandle source, InventoryHandle destination, Guid stack, int quantity, InventoryTransferOptions options) =>
            Source.SessionId == source.SessionId && Source.Reference.Equals(source.Reference) && Destination.Reference.Equals(destination.Reference) && Stack == stack && Quantity == quantity && Options.AllowPartial == options.AllowPartial && Options.IncludeFavourite == options.IncludeFavourite;
    }
}
