using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>
/// Keyed-owned reconciliation for authored pocket systems. One occurrence row per (owner, local, key):
/// occurrence key -> system guid + entrance gate + pocket gate + definition revision + declared gate
/// state. The API owns every native identity; re-declaring the same key reconciles to the owned
/// occurrence, never creates a duplicate, and never adopts a foreign or ambiguous native identity.
/// Reconstruction is a reconciled state invariant (load + later ticks converge), and one
/// ReconstructionSettled event reports actual outcomes once per session.
/// </summary>
internal sealed class PocketSystemCoordinator : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly PocketSystemRegistry _definitions;
    private readonly IPocketSystemNative _native;
    private readonly Func<bool> _canAuthor;
    private readonly Func<Guid, bool> _persistenceReady;
    private readonly Action<Exception> _report;
    private readonly IDisposable _subscription;
    private readonly Dictionary<(string Owner, string Local, string Key), PocketSystemOccurrence> _committed = new();
    private readonly Dictionary<(string Owner, string Local, string Key), PocketSystemOccurrence> _pending = new();
    private Action<ReconstructionSettledEvent>? _settled;
    private Guid _session;
    private bool _settledOnce;
    private bool _disposed;

    internal PocketSystemCoordinator(LifecycleHub hub, PocketSystemRegistry definitions, IPocketSystemNative native,
        Func<bool> canAuthor, Func<Guid, bool> persistenceReady, Action<Exception> report)
    {
        _hub = hub; _definitions = definitions; _native = native; _canAuthor = canAuthor;
        _persistenceReady = persistenceReady; _report = report;
        _subscription = hub.Subscribe("vgmodapi.authored-systems", e =>
        {
            if (e.Kind == LifecycleEventKind.SessionStarting && e.Session?.Id == _hub.CurrentSession?.Id)
                Reset();
            else if ((e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed) && e.Session?.Id == _session)
                Reset();
        });
    }
    internal void AttachSettled(Action<ReconstructionSettledEvent> settled)
    {
        _hub.CheckThread();
        _settled = settled ?? throw new ArgumentNullException(nameof(settled));
    }
    private void Reset()
    {
        _committed.Clear(); _pending.Clear(); _session = _hub.CurrentSession?.Id ?? Guid.Empty; _settledOnce = false;
    }
    private Guid Session() => _hub.CurrentSession?.Id ?? Guid.Empty;

    internal void RestoreRows(Guid session, PocketSystemOccurrence[] rows)
    {
        _hub.CheckThread();
        if (_disposed || session == Guid.Empty || session != Session()) throw new InvalidDataException("Stale authored-system restore.");
        _committed.Clear();
        if (rows != null)
            foreach (var row in rows)
                _committed[(row.Owner, row.LocalId, row.OccurrenceKey)] = row;
        _settledOnce = false;
    }

    internal PocketSystemOccurrence[] CaptureRows() { _hub.CheckThread(); return _committed.Values.ToArray(); }

    /// <summary>Read-only plumbing: the session-scoped occurrence rows this owner currently holds (committed + failed-pending), used to re-obtain surface objects.</summary>
    internal IReadOnlyList<PocketSystemOccurrence> Occurrences(string owner)
    {
        _hub.CheckThread();
        if (_disposed) return Array.Empty<PocketSystemOccurrence>();
        return _committed.Values.Where(o => o.Owner == owner).Concat(_pending.Values.Where(o => o.Owner == owner)).ToArray();
    }
    /// <summary>Read-only plumbing: whether an owned row (committed or failed-pending) exists for this key.</summary>
    internal bool ContainsOccurrence(string owner, string localId, string occurrenceKey)
    {
        _hub.CheckThread();
        if (_disposed) return false;
        return _committed.ContainsKey((owner, localId, occurrenceKey)) || _pending.ContainsKey((owner, localId, occurrenceKey));
    }
    /// <summary>Read-only plumbing: the owned row for a key (committed or failed-pending), if present.</summary>
    internal PocketSystemOccurrence? TryGetOccurrence(string owner, string localId, string occurrenceKey)
    {
        _hub.CheckThread();
        if (_disposed) return null;
        return _committed.TryGetValue((owner, localId, occurrenceKey), out var committed) ? committed
            : _pending.TryGetValue((owner, localId, occurrenceKey), out var pending) ? pending
            : null;
    }

    internal PocketSystemResult Create(PocketSystemRegistry.Provider provider, Guid expectedSession,
        string localId, string occurrenceKey, string anchorSystemId)
    {
        _hub.CheckThread();
        if (_disposed) return new PocketSystemResult(WorldStatus.Unavailable);
        if (provider == null || !_definitions.TryResolve(provider, localId, out var definition) || definition == null)
            return new PocketSystemResult(WorldStatus.NotRegistered);
        if (string.IsNullOrWhiteSpace(occurrenceKey)) return new PocketSystemResult(WorldStatus.InvalidDefinition);
        var key = (provider.Owner, localId, occurrenceKey);
        var reference = new PocketSystemReference(provider.Owner, localId, occurrenceKey);
        if (_committed.TryGetValue(key, out var owned))
        {
            var reconciled = Reconcile(owned);
            if (reconciled == null) return new PocketSystemResult(WorldStatus.Rejected, reference);
            return new PocketSystemResult(WorldStatus.Succeeded, reference, reconciled.SystemId, reconciled.EntranceGateId, reconciled.PocketGateId);
        }
        if (_pending.TryGetValue(key, out var attempted)) return new PocketSystemResult(WorldStatus.Rejected, reference,
            attempted.SystemId, attempted.EntranceGateId, attempted.PocketGateId);
        // Fail at Create (not save-time) once the owned envelope reaches its encode bound (1024 rows).
        if (_committed.Count + _pending.Count >= WorldSerializationAssociation.MaxObjects)
            return new PocketSystemResult(WorldStatus.Rejected, reference);
        // Allocate native identity only here; never adopt a foreign or ambiguous native identity.
        try
        {
            var info = _native.CreatePocket(expectedSession, anchorSystemId, definition.Placement, definition.FactionId, definition.Name);
            if (info == null)
            {
                _pending[key] = Failing(provider.Owner, localId, occurrenceKey, definition.Revision);
                return new PocketSystemResult(WorldStatus.Rejected, reference);
            }
            var occurrence = new PocketSystemOccurrence(provider.Owner, localId, occurrenceKey, definition.Revision,
                info.SystemId, info.EntranceGateId, info.PocketGateId, declaredOpen: false);
            _committed[key] = occurrence;
            return new PocketSystemResult(WorldStatus.Succeeded, reference, info.SystemId, info.EntranceGateId, info.PocketGateId);
        }
        catch (Exception error) { _report(error); return new PocketSystemResult(WorldStatus.Unavailable, reference); }
    }
    private static PocketSystemOccurrence Failing(string owner, string localId, string key, int revision)
        => new(owner, localId, key, revision, PendingSystemId(localId, key), "pending-entrance", "pending-pocket", declaredOpen: false);
    /// <summary>Projective, bounded placeholder native id for failed creations — never persisted, so it only needs to stay in the 128-byte encode bound.</summary>
    private static string PendingSystemId(string localId, string key)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("pending." + localId + "." + key));
        return "pending." + BitConverter.ToString(hash, 0, 12).Replace("-", "").ToLowerInvariant();
    }

    internal WorldStatus SetOpen(PocketSystemRegistry.Provider provider, Guid expectedSession, PocketSystemReference reference, bool open)
    {
        _hub.CheckThread();
        if (_disposed) return WorldStatus.Unavailable;
        if (reference == null || provider == null || reference.ProviderId != provider.Owner) return WorldStatus.NotRegistered;
        if (!_committed.TryGetValue((provider.Owner, reference.LocalId, reference.OccurrenceKey), out var occurrence))
            return WorldStatus.NotRegistered;
        try
        {
            // Apply to the native gate first; only commit the declared persistence state once the apply succeeds.
            if (!_native.ApplyOpen(expectedSession, occurrence.EntranceGateId, occurrence.PocketGateId, open)) return WorldStatus.Rejected;
            occurrence.DeclaredOpen = open;
            return WorldStatus.Succeeded;
        }
        catch (Exception error) { _report(error); return WorldStatus.Unavailable; }
    }

    /// <summary>
    /// Dissolves the owned occurrence: removes the native pocket (committed rows) and drops the row so
    /// save data no longer reconstructs it and the key becomes creatable again. A failed-creation
    /// pending row has no native pocket and is simply dropped. Refusals leave the row untouched.
    /// </summary>
    internal (WorldStatus Status, string Detail, string? SystemId) Dissolve(PocketSystemRegistry.Provider provider, Guid expectedSession, PocketSystemReference reference)
    {
        _hub.CheckThread();
        if (_disposed) return (WorldStatus.Unavailable, "Resource systems are unavailable.", null);
        if (reference == null || provider == null || reference.ProviderId != provider.Owner) return (WorldStatus.NotRegistered, "", null);
        var key = (provider.Owner, reference.LocalId, reference.OccurrenceKey);
        if (_pending.Remove(key)) return (WorldStatus.Succeeded, "", null); // failed creation: nothing native exists
        if (!_committed.TryGetValue(key, out var occurrence)) return (WorldStatus.NotRegistered, "", null);
        try
        {
            var outcome = _native.DissolvePocket(expectedSession, occurrence.SystemId, occurrence.EntranceGateId, occurrence.PocketGateId);
            switch (outcome)
            {
                case PocketDissolveOutcome.Dissolved:
                    _committed.Remove(key);
                    return (WorldStatus.Succeeded, "", occurrence.SystemId);
                case PocketDissolveOutcome.PlayerInside:
                    return (WorldStatus.Rejected, "The player's current system, location or a waypoint is inside the pocket; move the player out first.", null);
                case PocketDissolveOutcome.Missing:
                    return (WorldStatus.Rejected, "The pocket is not currently present natively; wait for reconstruction or check its state.", null);
                default:
                    return (WorldStatus.Rejected, "The native removal could not be performed or verified.", null);
            }
        }
        catch (Exception error) { _report(error); return (WorldStatus.Unavailable, "The native removal faulted.", null); }
    }

    internal PocketSystemState ReconstructionState(PocketSystemRegistry.Provider provider, PocketSystemReference reference)
    {
        _hub.CheckThread();
        if (_disposed || provider == null || reference == null || reference.ProviderId != provider.Owner)
            return new PocketSystemState(ReconstructionStatus.Pending);
        var key = (provider.Owner, reference.LocalId, reference.OccurrenceKey);
        if (_committed.TryGetValue(key, out var owned)) return Resolve(owned);
        if (_pending.TryGetValue(key, out var attempted))
            return Resolve(attempted);
        return new PocketSystemState(ReconstructionStatus.Pending);
    }

    /// <summary>Reconciled-invariant pass: idempotently converge reconstructed gates and finalize the once-per-session report.</summary>
    internal void Reconcile(Guid expectedSession)
    {
        _hub.CheckThread();
        if (_disposed || expectedSession == Guid.Empty || expectedSession != Session()) return;
        _native.BeginPass(expectedSession);
        try
        {
            foreach (var occurrence in _committed.Values.ToArray())
            {
                var state = Resolve(occurrence);
                if (state.Status == ReconstructionStatus.Reconstructed)
                {
                    try
                    {
                        if (!GateStateMatches(expectedSession, occurrence))
                            _native.ApplyOpen(expectedSession, occurrence.EntranceGateId, occurrence.PocketGateId, occurrence.DeclaredOpen);
                    }
                    catch (Exception error) { _report(error); }
                }
            }
            // Emit the once-per-session settlement at the gameplay boundary regardless of persistence
            // readiness so PersistenceUnavailable is genuinely reportable, not query-only.
            if (!_settledOnce && _hub.CurrentSession?.Phase == SessionPhase.GameplayInitialized)
            {
                _settledOnce = true;
                EmitSettled(expectedSession);
            }
        }
        finally { _native.EndPass(); }
    }

    private void EmitSettled(Guid session)
    {
        if (_settled == null) return;
        var failures = new List<ReconstructionFailure>();
        foreach (var pair in _committed)
        {
            var state = Resolve(pair.Value);
            var reason = state.Reason ?? (state.Status == ReconstructionStatus.Pending
                ? (_persistenceReady(session) ? ReconstructionFailureReason.NativeMissing : ReconstructionFailureReason.PersistenceUnavailable)
                : (ReconstructionFailureReason?)null);
            if (state.Status != ReconstructionStatus.Reconstructed && reason != null)
                failures.Add(new ReconstructionFailure(new PocketSystemReference(pair.Key.Owner, pair.Key.Local, pair.Key.Key), reason.Value));
        }
        foreach (var pair in _pending)
        {
            var state = Resolve(pair.Value);
            var reason = state.Reason ?? (state.Status == ReconstructionStatus.Pending
                ? (_persistenceReady(session) ? ReconstructionFailureReason.NativeMissing : ReconstructionFailureReason.PersistenceUnavailable)
                : (ReconstructionFailureReason?)null);
            if (state.Status != ReconstructionStatus.Reconstructed && reason != null)
                failures.Add(new ReconstructionFailure(new PocketSystemReference(pair.Key.Owner, pair.Key.Local, pair.Key.Key), reason.Value));
        }
        try { _settled(new ReconstructionSettledEvent(session, failures)); }
        catch (Exception error) { _report(error); }
    }

    /// <summary>
    /// The entrance-gate POI of a COMMITTED owned occurrence while it is reconstructed in the loaded
    /// game, or null. Story travel resolves destinations through this: an authored identity is never
    /// handed out while the world does not actually hold it.
    /// </summary>
    internal string? ResolveEntranceGate(string owner, string localId, string occurrenceKey)
    {
        _hub.CheckThread();
        if (_disposed || !_committed.TryGetValue((owner, localId, occurrenceKey), out var occurrence)) return null;
        var state = Resolve(occurrence);
        return state.Status == ReconstructionStatus.Reconstructed ? state.EntranceGatePoiId : null;
    }

    private PocketSystemOccurrence? Reconcile(PocketSystemOccurrence occurrence)
    {
        _hub.CheckThread();
        if (Resolve(occurrence).Status != ReconstructionStatus.Reconstructed) return null;
        try
        {
            if (!GateStateMatches(Session(), occurrence))
                _native.ApplyOpen(Session(), occurrence.EntranceGateId, occurrence.PocketGateId, occurrence.DeclaredOpen);
        }
        catch (Exception error) { _report(error); }
        return occurrence;
    }

    /// <summary>Whether the native gate pair currently presents the declared state. An open pocket must be
    /// open and visible; a closed pocket must be sealed (closed AND hidden), so a pocket authored before
    /// gates were hidden at create is repaired instead of leaving a phantom gate line on the map.</summary>
    private bool GateStateMatches(Guid session, PocketSystemOccurrence occurrence)
        => occurrence.DeclaredOpen
            ? _native.IsOpen(session, occurrence.EntranceGateId, occurrence.PocketGateId)
            : _native.IsSealed(session, occurrence.EntranceGateId, occurrence.PocketGateId);

    private PocketSystemState Resolve(PocketSystemOccurrence occurrence)
    {
        if (!_definitions.TryResolveMigration(occurrence.Owner, occurrence.LocalId, out var liveRevision, out var previousRevision))
            return new PocketSystemState(ReconstructionStatus.Failed, ReconstructionFailureReason.MissingDefinition);
        // Previous-revision migration: a retained row stamped with the immediately-previous revision is
        // the same owned occurrence under an upgraded definition, so migrate it up rather than failing.
        if (liveRevision != occurrence.Revision)
        {
            if (previousRevision.HasValue && previousRevision.Value == occurrence.Revision && previousRevision.Value < liveRevision)
                occurrence.MigrateRevision(liveRevision);
            else
                return new PocketSystemState(ReconstructionStatus.Failed, ReconstructionFailureReason.RevisionMismatch);
        }
        if (!_persistenceReady(Session()))
            return new PocketSystemState(ReconstructionStatus.Failed, ReconstructionFailureReason.PersistenceUnavailable);
        try
        {
            if (_native.AmbiguousCount(Session(), occurrence.SystemId) > 1)
                return new PocketSystemState(ReconstructionStatus.Failed, ReconstructionFailureReason.AmbiguousIdentity);
            var info = _native.ResolvePocket(Session(), occurrence.SystemId);
            if (info == null)
                return new PocketSystemState(ReconstructionStatus.Pending);
            if (info.EntranceGateId != occurrence.EntranceGateId || info.PocketGateId != occurrence.PocketGateId)
                return new PocketSystemState(ReconstructionStatus.Failed, ReconstructionFailureReason.AmbiguousIdentity);
            return new PocketSystemState(ReconstructionStatus.Reconstructed,
                systemId: info.SystemId, entranceGatePoiId: info.EntranceGateId, pocketGatePoiId: info.PocketGateId);
        }
        catch (Exception error) { _report(error); return new PocketSystemState(ReconstructionStatus.Failed, ReconstructionFailureReason.NativeMissing); }
    }

    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; _subscription.Dispose(); _committed.Clear(); _pending.Clear(); Reset();
    }
}
