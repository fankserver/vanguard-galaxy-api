using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonPodReturnObserver
{
    private readonly DungeonPodPersistence _state;
    private readonly DungeonPodResumeAdapter _pods;
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly Func<object, object> _origin;
    private readonly Func<object, bool> _ready;
    private readonly Func<object, Guid?> _operationId;
    private readonly DungeonReturnReceiptCollector _receipts = new();
    private readonly List<OverflowScope> _overflow = new();
    internal DungeonPodReturnObserver(DungeonPodPersistence state, DungeonPodResumeAdapter pods, IBoardingTacticalNativeBindings native,
        Func<object, object> origin, Func<object, bool> ready, Func<object, Guid?> operationId)
    { _operationId = operationId; _state = state; _pods = pods; _native = native; _origin = origin; _ready = ready; }
    internal bool CanArrive(object operation, object pod)
    {
        var data = _native.Get(pod, "resumePodData"); var id = data == null ? null : _pods.IdentityFor(data);
        if (!id.HasValue) return true;
        if (!_state.CanMutate || _pods.Conflicted(id.Value)) return false;
        var saved = _state.Get(id.Value); var ship = _native.Get(operation, "operationShip");
        if (saved == null || !saved.CanRecover || ship == null || !_ready(ship) || _operationId(operation) != saved.OperationId) return false;
        if ((string?)_native.Get(_native.Get(ship, "resumeShipData"), "resumeShipGuid") != saved.ParentShipId ||
            (_native.Get(data, "resumePodPlayer") is true) != saved.PlayerOwned || _native.Get(data, "resumePodPhase")?.ToString() != "Returning") return false;
        if (_native.Get(pod, "resumeReturnCrew") is not IReadOnlyDictionary<string, int> manifest || manifest.Count != saved.ReturnCrew.Count) return false;
        foreach (var pair in saved.ReturnCrew) if (!manifest.TryGetValue(pair.Key, out var count) || count != pair.Value) return false;
        return true;
    }
    internal bool Begin(object operation, object pod, out ReturnScope? scope)
    {
        scope = null;
        var data = _native.Get(pod, "resumePodData"); var id = data == null ? null : _pods.IdentityFor(data);
        if (!id.HasValue) { scope = new(null, _receipts.Begin(null, null)); return true; }
        if (_pods.Conflicted(id.Value)) return false;
        var ship = _native.Get(operation, "operationShip"); if (ship == null || !_ready(ship)) return false;
        var recipient = _native.Get(ship, "resumeShipData"); var shipId = (string?)_native.Get(recipient, "resumeShipGuid") ?? "";
        var saved = _state.Get(id.Value);
        if (saved == null || _operationId(operation) != saved.OperationId || (_native.Get(data, "resumePodPlayer") is true) != saved.PlayerOwned || _native.Get(data, "resumePodPhase")?.ToString() != "Arrived") return false;
        if (_native.Get(pod, "resumeReturnCrew") is not IReadOnlyDictionary<string, int> manifest || manifest.Count != saved.ReturnCrew.Count) return false;
        foreach (var pair in saved.ReturnCrew) if (!manifest.TryGetValue(pair.Key, out var count) || count != pair.Value) return false;
        var origin = _origin(ship);
        var attempt = _state.BeginObservedReturn(id.Value, shipId); if (attempt == null) return false;
        scope = new(attempt, _receipts.Begin(recipient, origin)); return true;
    }
    internal void CrewAdded(object recipient, string type, int amount, int overflow) => _receipts.CrewAdded(recipient, type, amount, overflow);
    internal OverflowScope BeginOverflow(object origin, string type, int amount)
    { var scope = new OverflowScope(this, origin, type, amount); _overflow.Add(scope); return scope; }
    internal void AddingPersistable(object poi, object data)
    {
        if (_overflow.Count == 0 || data.GetType().FullName != "Source.Data.Persistable.CrewPodData") return;
        var scope = _overflow[_overflow.Count - 1]; if (scope.Data != null) return;
        scope.Data = data; scope.Poi = poi;
    }
    internal void AddedPersistable(object poi, object data)
    {
        if (_overflow.Count == 0) return;
        var scope = _overflow[_overflow.Count - 1];
        if (ReferenceEquals(scope.Data, data) && ReferenceEquals(scope.Poi, poi) && _native.Get(poi, "persistables") is IList list && list.Contains(data)) scope.Persisted = true;
    }
    internal sealed class ReturnScope : IDisposable
    {
        private readonly DungeonPodPersistence.ReturnAttempt? _attempt;
        private readonly DungeonReturnReceiptCollector.Scope _receipt;
        internal ReturnScope(DungeonPodPersistence.ReturnAttempt? attempt, DungeonReturnReceiptCollector.Scope receipt) { _attempt = attempt; _receipt = receipt; }
        internal void Complete() { _attempt?.Complete(_receipt.Receipt); }
        public void Dispose() { _receipt.Dispose(); _attempt?.Dispose(); }
    }
    internal sealed class OverflowScope : IDisposable
    {
        private readonly DungeonPodReturnObserver _owner;
        private readonly object _origin; private readonly string _type; private readonly int _amount;
        internal object? Data, Poi; internal bool Persisted;
        internal OverflowScope(DungeonPodReturnObserver owner, object origin, string type, int amount) { _owner = owner; _origin = origin; _type = type; _amount = amount; }
        internal void Complete() { if (Persisted && Data != null) _owner._receipts.OverflowPersisted(_origin, _type, _amount, Data); }
        public void Dispose() => _owner._overflow.Remove(this);
    }
}
