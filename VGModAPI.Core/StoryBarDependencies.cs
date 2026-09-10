using System;
using System.Linq;

namespace VGModAPI.Core;

internal sealed partial class StoryContentService
{
    private int _barRegistrationDepth;
    private object _barOperationEpoch = new object();
    private object _barDependencyEpoch = new object();
    private object?[]? _barDependencySnapshot;

    internal void RefreshWorldDependencies()
    {
        CheckThread();
        var session = _currentSession()?.Id;
        if (_disposed || session == null || session != _restoredSession) return;
        var registry = _registry.Epoch;
        foreach (var entry in _ledger.Entries.ToArray())
        {
            if (entry.State == StoryOccurrenceState.Retired) continue;
            var definition = entry.RetainedDefinition;
            if (definition == null && !_registry.TryGet(entry.Id, out definition)) continue;
            if (!definition.Steps.SelectMany(step => step.Objectives).Any(objective => WorldObjectIdentity.IsReserved(objective.TargetPoiId))) continue;
            var missing = MissingTargets(entry.Id.Provider, definition, out var unknown);
            if (_disposed || _currentSession()?.Id != session || _restoredSession != session || !ReferenceEquals(registry, _registry.Epoch)) return;
            if (!_ledger.TryGet(entry.OccurrenceId, out var current) || !ReferenceEquals(entry, current)) return;
            if (unknown || missing != null) _unrunnable.Add(entry.OccurrenceId);
        }
        PublishAdmissions();
    }

    internal object BarDependencyStamp()
    {
        CheckThread();
        var session = _currentSession();
        var healthy = _protectionHealthy?.Invoke() != false;
        object?[] snapshot = { _registry.Epoch, _protection?.Epoch, _barOperationEpoch,
            _disposed, _fault, _suspended, _readiness, _restoredSession, session?.Id, healthy };
        if (_barDependencySnapshot == null || !_barDependencySnapshot.SequenceEqual(snapshot))
        {
            _barDependencySnapshot = snapshot;
            _barDependencyEpoch = new object();
        }
        return _barDependencyEpoch;
    }

    /// <summary>The unique currently ready occurrence of an authored definition; null when none or ambiguous.</summary>
    internal Guid? CurrentBarOccurrence(Guid expectedSession, StoryContentId definition)
    {
        CheckThread();
        Guid? found = null;
        foreach (var entry in _ledger.Entries)
        {
            if (entry.Id != definition || !IsBarMissionReady(expectedSession, definition, entry.OccurrenceId)) continue;
            if (found != null) return null;
            found = entry.OccurrenceId;
        }
        return found;
    }

    internal bool IsBarMissionReady(Guid expectedSession, StoryContentId definition, Guid occurrence)
    {
        CheckThread();
        var session = _currentSession();
        var healthy = _protectionHealthy?.Invoke() != false;
        // All provider/world callbacks have returned before examining the admission and identity.
        return healthy && !_disposed && _fault == null && _suspended == null && !InFlight && _barRegistrationDepth == 0
            && _readiness == Readiness.Restored && expectedSession == _restoredSession
            && session?.Id == expectedSession
            && _leasesBySegment.TryGetValue(definition.Provider, out var lease) && lease.Active
            && _registry.Contains(definition)
            && _ledger.TryGet(occurrence, out var entry) && entry.Id == definition
            && entry.State != StoryOccurrenceState.Retired && !_unrunnable.Contains(occurrence)
            && _protection?.IsAdmitted(expectedSession, StoryContentPolicy.OccurrenceIdentifier(definition, occurrence)) == true;
    }
}
