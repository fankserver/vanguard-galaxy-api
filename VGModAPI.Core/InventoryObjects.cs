using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed partial class InventoryService
{
    private readonly List<Move> _moves = new();
    internal IInventories ForGame(IGame game, Guid session) => new Inventories(this, game, session);

    internal void Tick()
    {
        _hub.CheckThread();
        if (_disposed || _busy || _serializationDepth != 0 || _saveDepth != 0 || _hub.IsDispatchingCallbacks ||
            !_hub.SaveOutcomes.Availability.IsAvailable) return;
        if (_pending != null)
        {
            try { Recover(_pending.Source.SessionId, _pending.Id); }
            catch (Exception error) { Report(error); }
        }
        var processed = 0;
        foreach (var move in _moves.ToArray())
        {
            if (!move.Game.IsActive) { move.End(InventoryTransferStatus.GameEnded); _moves.Remove(move); continue; }
            if (_pending != null || _busy || _serializationDepth != 0 || _saveDepth != 0) break;
            if (!move.Valid) move.End(InventoryTransferStatus.InvalidRequest);
            else
            {
                var result = Transfer(move.Id, move.Source.Handle, move.Destination!.Handle, move.Stack, move.Quantity, move.Options);
                move.SetResult(result);
            }
            if (move.Result.Status != InventoryTransferStatus.Pending) _moves.Remove(move);
            if (++processed >= 64) break;
        }
    }

    private sealed class Inventories : IInventories
    {
        private readonly InventoryService _owner;
        private readonly IGame _game;
        private readonly Guid _session;
        private readonly Dictionary<InventoryReference, Inventory> _inventories = new();
        internal Inventories(InventoryService owner, IGame game, Guid session)
        { _owner = owner; _game = game; _session = session; }
        public IGame Game { get { _owner._hub.CheckThread(); return _game; } }
        public InventoryDiscovery Discover()
        {
            _owner._hub.CheckThread();
            if (!Game.IsActive) return new InventoryDiscovery(InventoryTransferStatus.GameEnded, Array.Empty<IInventory>());
            var discovered = _owner.Discover(_session);
            return new InventoryDiscovery(discovered.Status, discovered.Inventories.Select(value => Get(value.Reference)));
        }
        public IInventory Get(InventoryReference reference)
        {
            _owner._hub.CheckThread();
            if (reference == null) throw new ArgumentNullException(nameof(reference));
            if (!_inventories.TryGetValue(reference, out var inventory))
            {
                inventory = new Inventory(_owner, _game, new InventoryHandle(_session, reference));
                _inventories.Add(reference, inventory);
            }
            return inventory;
        }
    }

    private sealed class Inventory : IInventory
    {
        internal readonly InventoryService Owner;
        internal readonly InventoryHandle Handle;
        private readonly IGame _game;
        internal Inventory(InventoryService owner, IGame game, InventoryHandle handle)
        { Owner = owner; _game = game; Handle = handle; }
        public IGame Game { get { Owner._hub.CheckThread(); return _game; } }
        public InventoryReference Reference { get { Owner._hub.CheckThread(); return Handle.Reference; } }
        public InventorySnapshot? Snapshot
        { get { Owner._hub.CheckThread(); return Game.IsActive ? Owner.Resolve(Handle.SessionId, Handle.Reference) : null; } }
        public IInventoryTransfer MoveTo(IInventory destination, Guid stackId, int quantity, InventoryTransferOptions? options = null)
        {
            Owner._hub.CheckThread();
            var move = new Move(Owner, this, destination as Inventory, stackId, quantity, options ?? new InventoryTransferOptions());
            if (Owner._disposed || !Game.IsActive) move.End(InventoryTransferStatus.GameEnded);
            else Owner._moves.Add(move);
            return move;
        }
    }

    private sealed class Move : IInventoryTransfer
    {
        private readonly InventoryService _owner;
        private readonly List<Handler> _handlers = new();
        private InventoryTransferResult _result;
        private bool _completed;
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly Inventory Source;
        internal readonly Inventory? Destination;
        internal readonly Guid Stack;
        internal readonly int Quantity;
        internal readonly InventoryTransferOptions Options;
        internal bool Valid => Destination != null && ReferenceEquals(Source.Owner, Destination.Owner) &&
            ReferenceEquals(Source.Game, Destination.Game) && !Source.Reference.Equals(Destination.Reference) &&
            Stack != Guid.Empty && Quantity > 0 && Quantity <= 100000;
        internal Move(InventoryService owner, Inventory source, Inventory? destination, Guid stack, int quantity, InventoryTransferOptions options)
        {
            _owner = owner; Source = source; Destination = destination; Stack = stack; Quantity = quantity; Options = options;
            _result = new InventoryTransferResult(Id, InventoryTransferStatus.Pending, quantity, null, null, null);
        }
        public IGame Game => Source.Game;
        public InventoryTransferResult Result { get { _owner._hub.CheckThread(); return _result; } }
        public event Action<IInventoryTransfer>? Completed
        {
            add
            {
                _owner._hub.CheckThread();
                if (value != null) foreach (Action<IInventoryTransfer> callback in value.GetInvocationList()) _handlers.Add(new Handler(callback));
            }
            remove
            {
                _owner._hub.CheckThread();
                if (value == null) return;
                var removed = value.GetInvocationList();
                for (var start = _handlers.Count - removed.Length; start >= 0; start--)
                {
                    var matches = true;
                    for (var index = 0; index < removed.Length; index++)
                        if (!_handlers[start + index].Callback.Equals(removed[index])) { matches = false; break; }
                    if (!matches) continue;
                    for (var index = 0; index < removed.Length; index++) _handlers[start + index].Active = false;
                    _handlers.RemoveRange(start, removed.Length); return;
                }
            }
        }
        internal void End(InventoryTransferStatus status) => SetResult(status == InventoryTransferStatus.GameEnded
            ? new InventoryTransferResult(Id, status, Quantity, null, null, null)
            : new InventoryTransferResult(Id, status, Quantity, 0, 0, 0));
        internal void SetResult(InventoryTransferResult result)
        {
            if (_completed) return;
            _result = result;
            if (result.Status == InventoryTransferStatus.Pending) return;
            _completed = true;
            foreach (var handler in _handlers.ToArray())
                _owner._hub.Gameplay.Enqueue(Source.Handle.SessionId,
                    handler.Callback.Method.Module.Assembly.GetName().Name ?? "inventory subscriber",
                    () => handler.Callback(this), () => !_owner._disposed && handler.Active);
        }
        private sealed class Handler
        {
            internal readonly Action<IInventoryTransfer> Callback;
            internal bool Active = true;
            internal Handler(Action<IInventoryTransfer> callback) { Callback = callback; }
        }
    }
}
