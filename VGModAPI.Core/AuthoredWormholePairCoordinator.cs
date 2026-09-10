using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class AuthoredWormholePairCoordinator : IDisposable
{
    private readonly LifecycleHub _hub; private readonly AuthoredWormholePairRegistry _definitions; private readonly IAuthoredWormholePairNative _native;
    private readonly Func<Guid, bool> _persistenceReady; private readonly Action<Exception> _report; private readonly IDisposable _subscription;
    private readonly Dictionary<(string Owner, string Local, string Key), AuthoredWormholePairOccurrence> _rows = new();
    private Action<Guid>? _settled; private Guid _session; private bool _settledOnce, _disposed;
    internal AuthoredWormholePairCoordinator(LifecycleHub hub, AuthoredWormholePairRegistry definitions, IAuthoredWormholePairNative native,
        Func<Guid, bool> persistenceReady, Action<Exception> report)
    {
        _hub = hub; _definitions = definitions; _native = native; _persistenceReady = persistenceReady; _report = report;
        _subscription = hub.Subscribe("vgmodapi.authored-wormholes", e =>
        {
            if (e.Kind == LifecycleEventKind.SessionStarting && e.Session?.Id == hub.CurrentSession?.Id) Reset();
            else if ((e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed) && e.Session?.Id == _session) Reset();
        });
    }
    private Guid Session() => _hub.CurrentSession?.Id ?? Guid.Empty;
    private void Reset() { _rows.Clear(); _session = Session(); _settledOnce = false; }
    internal void AttachSettled(Action<Guid> settled) { _hub.CheckThread(); _settled = settled; }
    internal void RestoreRows(Guid session, AuthoredWormholePairOccurrence[] rows)
    {
        _hub.CheckThread(); if (_disposed || session == Guid.Empty || session != Session()) throw new InvalidDataException("Stale authored-wormhole restore.");
        _rows.Clear(); foreach (var row in rows ?? Array.Empty<AuthoredWormholePairOccurrence>()) _rows[(row.Owner, row.LocalId, row.OccurrenceKey)] = row; _settledOnce = false;
    }
    internal AuthoredWormholePairOccurrence[] CaptureRows() { _hub.CheckThread(); return _rows.Values.ToArray(); }
    internal IReadOnlyList<AuthoredWormholePairOccurrence> Occurrences(string owner) { _hub.CheckThread(); return _rows.Values.Where(r => r.Owner == owner).ToArray(); }
    internal AuthoredWormholePairOccurrence? TryGet(string owner, string local, string key)
        => _rows.TryGetValue((owner, local, key), out var row) ? row : null;
    internal bool Contains(string owner, string local, string key) => _rows.ContainsKey((owner, local, key));
    internal (WorldStatus Status, AuthoredWormholePairOccurrence? Row) Create(AuthoredWormholePairRegistry.Provider provider, Guid session,
        string localId, string occurrenceKey, string firstSystemId, string secondSystemId)
    {
        _hub.CheckThread(); if (_disposed) return (WorldStatus.Unavailable, null);
        if (!_definitions.TryResolve(provider, localId, out var definition) || definition == null) return (WorldStatus.NotRegistered, null);
        if (string.IsNullOrWhiteSpace(occurrenceKey) || firstSystemId == secondSystemId) return (WorldStatus.InvalidDefinition, null);
        var key = (provider.Owner, localId, occurrenceKey);
        if (_rows.TryGetValue(key, out var existing)) return Resolve(existing).Reconstructed ? (WorldStatus.Succeeded, existing) : (WorldStatus.Rejected, existing);
        if (_rows.Count >= WorldSerializationAssociation.MaxObjects) return (WorldStatus.Rejected, null);
        try
        {
            var info = _native.CreatePair(session, definition.Name, firstSystemId, secondSystemId, open: true);
            if (info == null) return (WorldStatus.Rejected, null);
            var row = new AuthoredWormholePairOccurrence(provider.Owner, localId, occurrenceKey, definition.Revision,
                firstSystemId, secondSystemId, info.FirstPoiId, info.SecondPoiId, declaredOpen: true);
            _rows.Add(key, row); return (WorldStatus.Succeeded, row);
        }
        catch (Exception error) { _report(error); return (WorldStatus.Unavailable, null); }
    }
    internal WorldStatus SetOpen(AuthoredWormholePairRegistry.Provider provider, Guid session, string local, string key, bool open)
    {
        _hub.CheckThread(); if (!_rows.TryGetValue((provider.Owner, local, key), out var row)) return WorldStatus.NotRegistered;
        if (!Resolve(row).Reconstructed) return WorldStatus.Rejected;
        try { if (!_native.ApplyOpen(session, row.FirstPoiId, row.SecondPoiId, open)) return WorldStatus.Rejected; row.DeclaredOpen = open; return WorldStatus.Succeeded; }
        catch (Exception error) { _report(error); return WorldStatus.Unavailable; }
    }
    internal AuthoredWormholePairState State(AuthoredWormholePairRegistry.Provider provider, string local, string key)
    { _hub.CheckThread(); return _rows.TryGetValue((provider.Owner, local, key), out var row) ? Resolve(row) : new(AuthoredSystemReconstructionStatus.Pending); }
    private AuthoredWormholePairState Resolve(AuthoredWormholePairOccurrence row)
    {
        if (!_definitions.TryResolveMigration(row.Owner, row.LocalId, out var live, out var previous)) return new(AuthoredSystemReconstructionStatus.Failed, AuthoredWormholePairFailureReason.MissingDefinition);
        if (live != row.Revision)
        {
            if (previous == row.Revision && previous < live) row.MigrateRevision(live);
            else return new(AuthoredSystemReconstructionStatus.Failed, AuthoredWormholePairFailureReason.RevisionMismatch);
        }
        if (!_persistenceReady(Session())) return new(AuthoredSystemReconstructionStatus.Failed, AuthoredWormholePairFailureReason.PersistenceUnavailable);
        try
        {
            if (_native.AmbiguousCount(Session(), row.FirstPoiId) > 1 || _native.AmbiguousCount(Session(), row.SecondPoiId) > 1)
                return new(AuthoredSystemReconstructionStatus.Failed, AuthoredWormholePairFailureReason.AmbiguousIdentity);
            var info = _native.ResolvePair(Session(), row.FirstSystemId, row.SecondSystemId, row.FirstPoiId, row.SecondPoiId);
            return info == null ? new(AuthoredSystemReconstructionStatus.Pending) :
                new(AuthoredSystemReconstructionStatus.Reconstructed, firstWormholePoiId: info.FirstPoiId, secondWormholePoiId: info.SecondPoiId);
        }
        catch (Exception error) { _report(error); return new(AuthoredSystemReconstructionStatus.Failed, AuthoredWormholePairFailureReason.NativeMissing); }
    }
    internal void Reconcile(Guid session)
    {
        _hub.CheckThread(); if (_disposed || session == Guid.Empty || session != Session()) return;
        _native.BeginPass(session);
        try
        {
            foreach (var row in _rows.Values)
            {
                var state = Resolve(row); if (!state.Reconstructed) continue;
                var current = _native.ResolvePair(session, row.FirstSystemId, row.SecondSystemId, row.FirstPoiId, row.SecondPoiId);
                if (current != null && current.Open != row.DeclaredOpen) _native.ApplyOpen(session, row.FirstPoiId, row.SecondPoiId, row.DeclaredOpen);
            }
            if (!_settledOnce && _hub.CurrentSession?.Phase == SessionPhase.GameplayInitialized) { _settledOnce = true; _settled?.Invoke(session); }
        }
        finally { _native.EndPass(); }
    }
    public void Dispose() { _hub.CheckThread(); if (_disposed) return; _disposed = true; _subscription.Dispose(); _rows.Clear(); }
}
