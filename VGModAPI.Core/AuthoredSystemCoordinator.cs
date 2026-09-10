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
internal sealed class AuthoredSystemCoordinator : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly AuthoredSystemRegistry _definitions;
    private readonly IAuthoredSystemNative _native;
    private readonly Func<bool> _canAuthor;
    private readonly Func<Guid, bool> _persistenceReady;
    private readonly Action<Exception> _report;
    private readonly IDisposable _subscription;
    private readonly Dictionary<(string Owner, string Local, string Key), AuthoredSystemOccurrence> _committed = new();
    private readonly Dictionary<(string Owner, string Local, string Key), AuthoredSystemOccurrence> _pending = new();
    private Action<ReconstructionSettledEvent>? _settled;
    private Guid _session;
    private bool _settledOnce;
    private bool _disposed;

    internal AuthoredSystemCoordinator(LifecycleHub hub, AuthoredSystemRegistry definitions, IAuthoredSystemNative native,
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

    internal void RestoreRows(Guid session, AuthoredSystemOccurrence[] rows)
    {
        _hub.CheckThread();
        if (_disposed || session == Guid.Empty || session != Session()) throw new InvalidDataException("Stale authored-system restore.");
        _committed.Clear();
        if (rows != null)
            foreach (var row in rows)
                _committed[(row.Owner, row.LocalId, row.OccurrenceKey)] = row;
        _settledOnce = false;
    }

    internal byte[] CaptureBytes()
    {
        _hub.CheckThread();
        return AuthoredSystemStateCodec.Encode(_committed.Values.ToArray());
    }
    internal AuthoredSystemOccurrence[] CaptureRows() { _hub.CheckThread(); return _committed.Values.ToArray(); }

    /// <summary>Read-only plumbing: the session-scoped occurrence rows this owner currently holds (committed + failed-pending), used to re-obtain surface objects.</summary>
    internal IReadOnlyList<AuthoredSystemOccurrence> Occurrences(string owner)
    {
        _hub.CheckThread();
        if (_disposed) return Array.Empty<AuthoredSystemOccurrence>();
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
    internal AuthoredSystemOccurrence? TryGetOccurrence(string owner, string localId, string occurrenceKey)
    {
        _hub.CheckThread();
        if (_disposed) return null;
        return _committed.TryGetValue((owner, localId, occurrenceKey), out var committed) ? committed
            : _pending.TryGetValue((owner, localId, occurrenceKey), out var pending) ? pending
            : null;
    }

    internal AuthoredSystemResult Create(AuthoredSystemRegistry.Provider provider, Guid expectedSession,
        string localId, string occurrenceKey, string anchorSystemId)
    {
        _hub.CheckThread();
        if (_disposed) return new AuthoredSystemResult(WorldStatus.Unavailable);
        if (provider == null || !_definitions.TryResolve(provider, localId, out var definition) || definition == null)
            return new AuthoredSystemResult(WorldStatus.NotRegistered);
        if (string.IsNullOrWhiteSpace(occurrenceKey)) return new AuthoredSystemResult(WorldStatus.InvalidDefinition);
        var key = (provider.Owner, localId, occurrenceKey);
        var reference = new AuthoredSystemReference(provider.Owner, localId, occurrenceKey);
        if (_committed.TryGetValue(key, out var owned))
        {
            var reconciled = Reconcile(owned);
            if (reconciled == null) return new AuthoredSystemResult(WorldStatus.Rejected, reference);
            return new AuthoredSystemResult(WorldStatus.Succeeded, reference, reconciled.SystemId, reconciled.EntranceGateId, reconciled.PocketGateId);
        }
        if (_pending.TryGetValue(key, out var attempted)) return new AuthoredSystemResult(WorldStatus.Rejected, reference,
            attempted.SystemId, attempted.EntranceGateId, attempted.PocketGateId);
        // Fail at Create (not save-time) once the owned envelope reaches its encode bound (1024 rows).
        if (_committed.Count + _pending.Count >= WorldSerializationAssociation.MaxObjects)
            return new AuthoredSystemResult(WorldStatus.Rejected, reference);
        // Allocate native identity only here; never adopt a foreign or ambiguous native identity.
        try
        {
            var info = _native.CreatePocket(expectedSession, anchorSystemId);
            if (info == null)
            {
                _pending[key] = Failing(provider.Owner, localId, occurrenceKey, definition.Revision);
                return new AuthoredSystemResult(WorldStatus.Rejected, reference);
            }
            var occurrence = new AuthoredSystemOccurrence(provider.Owner, localId, occurrenceKey, definition.Revision,
                info.SystemId, info.EntranceGateId, info.PocketGateId, declaredOpen: false);
            _committed[key] = occurrence;
            return new AuthoredSystemResult(WorldStatus.Succeeded, reference, info.SystemId, info.EntranceGateId, info.PocketGateId);
        }
        catch (Exception error) { _report(error); return new AuthoredSystemResult(WorldStatus.Unavailable, reference); }
    }
    private static AuthoredSystemOccurrence Failing(string owner, string localId, string key, int revision)
        => new(owner, localId, key, revision, PendingSystemId(localId, key), "pending-entrance", "pending-pocket", declaredOpen: false);
    /// <summary>Projective, bounded placeholder native id for failed creations — never persisted, so it only needs to stay in the 128-byte encode bound.</summary>
    private static string PendingSystemId(string localId, string key)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("pending." + localId + "." + key));
        return "pending." + BitConverter.ToString(hash, 0, 12).Replace("-", "").ToLowerInvariant();
    }

    internal WorldStatus SetOpen(AuthoredSystemRegistry.Provider provider, Guid expectedSession, AuthoredSystemReference reference, bool open)
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
    internal (WorldStatus Status, string Detail, string? SystemId) Dissolve(AuthoredSystemRegistry.Provider provider, Guid expectedSession, AuthoredSystemReference reference)
    {
        _hub.CheckThread();
        if (_disposed) return (WorldStatus.Unavailable, "Authored systems are unavailable.", null);
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

    internal AuthoredSystemReconstructionState ReconstructionState(AuthoredSystemRegistry.Provider provider, AuthoredSystemReference reference)
    {
        _hub.CheckThread();
        if (_disposed || provider == null || reference == null || reference.ProviderId != provider.Owner)
            return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Pending);
        var key = (provider.Owner, reference.LocalId, reference.OccurrenceKey);
        if (_committed.TryGetValue(key, out var owned)) return Resolve(owned);
        if (_pending.TryGetValue(key, out var attempted))
            return Resolve(attempted);
        return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Pending);
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
                if (state.Status == AuthoredSystemReconstructionStatus.Reconstructed)
                {
                    try
                    {
                        if (occurrence.DeclaredOpen != _native.IsOpen(expectedSession, occurrence.EntranceGateId, occurrence.PocketGateId))
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
        var failures = new List<AuthoredSystemFailure>();
        foreach (var pair in _committed)
        {
            var state = Resolve(pair.Value);
            var reason = state.Reason ?? (state.Status == AuthoredSystemReconstructionStatus.Pending
                ? (_persistenceReady(session) ? AuthoredSystemFailureReason.NativeMissing : AuthoredSystemFailureReason.PersistenceUnavailable)
                : (AuthoredSystemFailureReason?)null);
            if (state.Status != AuthoredSystemReconstructionStatus.Reconstructed && reason != null)
                failures.Add(new AuthoredSystemFailure(new AuthoredSystemReference(pair.Key.Owner, pair.Key.Local, pair.Key.Key), reason.Value));
        }
        foreach (var pair in _pending)
        {
            var state = Resolve(pair.Value);
            var reason = state.Reason ?? (state.Status == AuthoredSystemReconstructionStatus.Pending
                ? (_persistenceReady(session) ? AuthoredSystemFailureReason.NativeMissing : AuthoredSystemFailureReason.PersistenceUnavailable)
                : (AuthoredSystemFailureReason?)null);
            if (state.Status != AuthoredSystemReconstructionStatus.Reconstructed && reason != null)
                failures.Add(new AuthoredSystemFailure(new AuthoredSystemReference(pair.Key.Owner, pair.Key.Local, pair.Key.Key), reason.Value));
        }
        try { _settled(new ReconstructionSettledEvent(session, failures)); }
        catch (Exception error) { _report(error); }
    }

    private AuthoredSystemOccurrence? Reconcile(AuthoredSystemOccurrence occurrence)
    {
        _hub.CheckThread();
        if (Resolve(occurrence).Status != AuthoredSystemReconstructionStatus.Reconstructed) return null;
        try
        {
            if (occurrence.DeclaredOpen != _native.IsOpen(Session(), occurrence.EntranceGateId, occurrence.PocketGateId))
                _native.ApplyOpen(Session(), occurrence.EntranceGateId, occurrence.PocketGateId, occurrence.DeclaredOpen);
        }
        catch (Exception error) { _report(error); }
        return occurrence;
    }

    private AuthoredSystemReconstructionState Resolve(AuthoredSystemOccurrence occurrence)
    {
        if (!_definitions.TryResolveMigration(occurrence.Owner, occurrence.LocalId, out var liveRevision, out var previousRevision))
            return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Failed, AuthoredSystemFailureReason.MissingDefinition);
        // Previous-revision migration: a retained row stamped with the immediately-previous revision is
        // the same owned occurrence under an upgraded definition, so migrate it up rather than failing.
        if (liveRevision != occurrence.Revision)
        {
            if (previousRevision.HasValue && previousRevision.Value == occurrence.Revision && previousRevision.Value < liveRevision)
                occurrence.MigrateRevision(liveRevision);
            else
                return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Failed, AuthoredSystemFailureReason.RevisionMismatch);
        }
        if (!_persistenceReady(Session()))
            return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Failed, AuthoredSystemFailureReason.PersistenceUnavailable);
        try
        {
            if (_native.AmbiguousCount(Session(), occurrence.SystemId) > 1)
                return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Failed, AuthoredSystemFailureReason.AmbiguousIdentity);
            var info = _native.ResolvePocket(Session(), occurrence.SystemId);
            if (info == null)
                return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Pending);
            if (info.EntranceGateId != occurrence.EntranceGateId || info.PocketGateId != occurrence.PocketGateId)
                return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Failed, AuthoredSystemFailureReason.AmbiguousIdentity);
            return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Reconstructed,
                systemId: info.SystemId, entranceGatePoiId: info.EntranceGateId, pocketGatePoiId: info.PocketGateId);
        }
        catch (Exception error) { _report(error); return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Failed, AuthoredSystemFailureReason.NativeMissing); }
    }

    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; _subscription.Dispose(); _committed.Clear(); _pending.Clear(); Reset();
    }
}
