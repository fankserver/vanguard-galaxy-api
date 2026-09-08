using System;
using System.Runtime.CompilerServices;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonOperationResumeAdapter
{
    private readonly DungeonPodPersistence _state;
    private readonly IBoardingTacticalNativeBindings _native;
    private ConditionalWeakTable<object, Identity> _operations = new(), _locations = new();
    internal DungeonOperationResumeAdapter(DungeonPodPersistence state, IBoardingTacticalNativeBindings native) { _state = state; _native = native; }
    private readonly System.Collections.Generic.Dictionary<Guid, WeakReference<object>> _bound = new();
    private readonly System.Collections.Generic.HashSet<Guid> _conflicts = new();
    internal void Clear() { _operations = new(); _locations = new(); _bound.Clear(); _conflicts.Clear(); }
    internal bool Conflicted(Guid id) => _conflicts.Contains(id);
    internal Guid? OperationId(object operation) => _operations.TryGetValue(operation, out var value) ? value.Id : null;
    internal Guid? LocationMarker(object location) => _locations.TryGetValue(location, out var value) ? value.Id : null;
    internal void LoadedLocation(object location, Guid operationId)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Missing saved operation identity.");
        _locations.Remove(location); _locations.Add(location, new(operationId));
    }
    internal Guid? Created(object operation, Guid? contentOccurrence, string missionProtection)
    {
        if (!_state.CanMutate) return null;
        if (OperationId(operation) is { } known) return _conflicts.Contains(known) ? null : known;
        var location = _native.Get(operation, "location") ?? throw new InvalidOperationException("Missing operation location.");
        var shipData = _native.Get(_native.Get(operation, "operationShip"), "resumeShipData");
        var shipId = (string?)_native.Get(shipData, "resumeShipGuid") ?? "";
        var id = Guid.NewGuid(); var previous = LocationMarker(location);
        if (previous.HasValue && (_state.Operation(previous.Value) == null || _conflicts.Contains(previous.Value))) return null;
        var locationId = previous.HasValue ? _state.Operation(previous.Value)?.LocationId ?? Guid.NewGuid() : Guid.NewGuid();
        var state = new DungeonOperationResumeState(id, locationId, contentOccurrence, shipId,
            _native.Get(location, "dungeonType")?.ToString() ?? "", _native.Get(operation, "phase")?.ToString() ?? "",
            _native.Get(_native.Get(operation, "simulation"), "outcome")?.ToString() ?? "", missionProtection, DungeonTerminalProgress.NotStarted,
            _native.Get(operation, "isAutonomous") is true);
        if (!_state.TrackOperation(state)) return null;
        Bind(operation, id); LoadedLocation(location, id); return id;
    }
    internal bool Resumed(object operation)
    {
        if (!_state.CanMutate) return false;
        var location = _native.Get(operation, "location"); if (location == null) return false;
        var id = LocationMarker(location); var saved = id.HasValue ? _state.Operation(id.Value) : null;
        if (saved == null || _conflicts.Contains(saved.Id) || (saved.TerminalProgress != DungeonTerminalProgress.NotStarted && !saved.MayResumeWalkExtraction)) return false;
        var ship = _native.Get(_native.Get(operation, "operationShip"), "resumeShipData");
        if ((string?)_native.Get(ship, "resumeShipGuid") != saved.AttackerShipId ||
            (_native.Get(operation, "isAutonomous") is true) != saved.Autonomous || _native.Get(location, "dungeonType")?.ToString() != saved.DungeonType) return false;
        if (OperationId(operation) is { } known) return known == saved.Id;
        return Bind(operation, saved.Id);
    }
    internal void BindReturnCarrier(object operation, Guid id)
    {
        if (!_state.CanMutate || _state.Operation(id) == null || !DungeonReturnCarrier.IsSettlementOnly(operation)) throw new InvalidOperationException("Invalid return-only operation binding.");
        if (OperationId(operation) is { } known)
        { if (known != id) throw new InvalidOperationException("Return carrier identity cannot change."); return; }
        _operations.Add(operation, new(id));
    }
    private bool Bind(object operation, Guid id)
    {
        if (_bound.TryGetValue(id, out var reference) && reference.TryGetTarget(out var existing) && !ReferenceEquals(existing, operation))
        { _conflicts.Add(id); return false; }
        _bound[id] = new(operation); _operations.Add(operation, new(id)); return true;
    }
    private sealed class Identity { internal readonly Guid Id; internal Identity(Guid id) { Id = id; } }
}
