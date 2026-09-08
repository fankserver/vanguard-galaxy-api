using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>Automatic save participation for pod obligations. Consumers supply no serializers.</summary>
internal sealed class DungeonPodPersistence : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly DungeonPodRecoveryLedger _ledger = new();
    private readonly DungeonOperationRecoveryLedger _operations = new();
    private readonly IPersistenceRegistration? _registration;
    private readonly IDisposable _lifetime;
    private Guid? _restoredSession;
    private bool _disposed;
    private int _returnDepth;
    private Guid? _terminalOperation, _cancellationOperation;
    private object? _terminalToken, _cancellationToken;
    internal Cancellation? BeginCancellation(Guid id)
    {
        if (!CanMutate || _operations.Get(id) == null) return null;
        var lease = _effects.Enter(); _cancellationOperation = id; _cancellationToken = RestoreToken; return new(this, lease);
    }
    internal sealed class Cancellation : IDisposable
    {
        private readonly DungeonPodPersistence _owner; private readonly DungeonMutationFence.Lease _lease; private bool _disposed;
        internal Cancellation(DungeonPodPersistence owner, DungeonMutationFence.Lease lease) { _owner = owner; _lease = lease; }
        internal void Failed() => _lease.Failed();
        public void Dispose() { if (_disposed) return; _disposed = true; _lease.Dispose(); _owner._cancellationOperation = null; }
    }
    internal bool IsCheckpointing { get; private set; }
    internal bool CanObserveSnapshots => CanMutate || (IsCheckpointing && Ready);
    internal void Checkpoint(Action capture)
    {
        EnsureSerializationAllowed();
        if (!Ready || IsCheckpointing) throw new InvalidOperationException("Recovery checkpoint unavailable.");
        IsCheckpointing = true;
        try { capture(); } finally { IsCheckpointing = false; }
    }
    private readonly DungeonMutationFence _effects = new();
    internal void RejectTransferSnapshot() { _hub.CheckThread(); _effects.Fail(); }
    internal DungeonMutationFence.Lease? BeginTransfer()
    {
        if (IsCheckpointing || _returnDepth != 0 || !Ready || !_registration!.MutationAllowed || _hub.IsDispatchingCallbacks || _effects.Uncertain) return null;
        return _effects.Enter();
    }
    internal object RestoreToken { get; private set; } = new();
    private readonly HashSet<Guid> _restoredPods = new();
    internal bool WasRestored(Guid id) => Ready && _restoredPods.Contains(id);
    internal DungeonPodPersistence(LifecycleHub hub, IPersistenceApi? persistence)
    {
        _hub = hub;
        _lifetime = hub.Subscribe("vgmodapi.dungeon-pods", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            { _restoredSession = null; _ledger.Restore(null); _operations.Restore(null); _restoredPods.Clear(); RestoreToken = new(); _effects.Reset(); }
        });
        _registration = persistence?.Register(new PersistenceProvider("vgmodapi.dungeon-recovery", 1, Capture, Restore, DungeonRecoveryCodec.Validate));
    }
    internal bool Ready
    {
        get
        {
            _hub.CheckThread(); return !_disposed && _restoredSession.HasValue && _restoredSession == _hub.CurrentSession?.Id &&
                _registration is IPersistenceReadiness readiness && readiness.StateReady;
        }
    }
    internal bool CanMutate => !IsCheckpointing && !_effects.Busy && !_effects.Uncertain && _returnDepth == 0 && Ready && _registration!.MutationAllowed && !_hub.IsDispatchingCallbacks;
    internal IReadOnlyList<DungeonPodResumeState> Snapshot => Ready ? _ledger.Snapshot : Array.Empty<DungeonPodResumeState>();
    internal DungeonPodResumeState? Get(Guid id) => Ready ? _ledger.Get(id) : null;
    internal DungeonOperationResumeState? Operation(Guid id) => Ready ? _operations.Get(id) : null;
    internal bool TrackOperation(DungeonOperationResumeState state)
    {
        if (!CanObserveSnapshots || (IsCheckpointing && (_operations.Get(state.Id) is not { } previous || previous.TerminalProgress != state.TerminalProgress || (previous.WalkReturn?.Progress ?? DungeonWalkReturnProgress.Pending) != (state.WalkReturn?.Progress ?? DungeonWalkReturnProgress.Pending)))) return false;
        _operations.Track(state); return true;
    }
    internal bool RefreshTransportPose(Guid id, IReadOnlyList<float> pose)
    {
        EnsureSerializationAllowed();
        if (!Ready || _ledger.Get(id) is not { Transport: { } transport } pod) return false;
        var refreshed = new DungeonPodTransport(transport.NativePodId, transport.PendingReinforcement, transport.OutboundCrew, pose, transport.DonorShipId);
        _ledger.Track(new(pod.Id, pod.OperationId, pod.Phase, pod.PlayerOwned, pod.ReturnManifestKnown, pod.ReturnDelivered, pod.ReturnCrew, pod.ReturnAttempted, pod.ParentShipId, refreshed));
        return true;
    }
    internal bool Track(DungeonPodResumeState state)
    {
        if (!CanObserveSnapshots || _operations.Get(state.OperationId) == null || (IsCheckpointing && (_ledger.Get(state.Id) is not { } previous || previous.ReturnAttempted != state.ReturnAttempted || previous.ReturnDelivered != state.ReturnDelivered))) return false;
        _ledger.Track(state); return true;
    }
    internal DungeonPodResumeState? BeginReturn(Guid id) => CanMutate ? _ledger.BeginReturn(id) : null;
    internal bool Return(Guid id, string parentShipId, Func<IReadOnlyDictionary<string, int>, DungeonPodDeliveryReceipt?> nativeReturn)
    {
        if (nativeReturn == null) throw new ArgumentNullException(nameof(nativeReturn));
        if (!CanMutate) return false;
        var pod = _ledger.Get(id);
        if (pod == null || string.IsNullOrEmpty(pod.ParentShipId) || pod.ParentShipId != parentShipId) return false;
        var session = _restoredSession;
        if (_ledger.BeginReturn(id) == null) return false;
        var attempted = _ledger.Get(id);
        DungeonPodDeliveryReceipt? receipt;
        _returnDepth++;
        try { receipt = nativeReturn(pod.ReturnCrew); }
        finally { _returnDepth--; }
        if (receipt == null || !receipt.AccountsFor(pod.ReturnCrew)) return false;
        if (!CanMutate || _restoredSession != session || !ReferenceEquals(_ledger.Get(id), attempted)) return false;
        _ledger.Delivered(id); return true;
    }
    internal void EnsureSerializationAllowed()
    { _hub.CheckThread(); _effects.EnsureSettled(); if (_returnDepth != 0 || IsCheckpointing) throw new InvalidOperationException("Cannot save while dungeon effects or snapshot refresh are being applied."); }
    internal bool CompleteDonorAbort(Guid operationId, string shipId, object token)
    {
        if (!CanMutate || !ReferenceEquals(token, RestoreToken) || _operations.Get(operationId) is not { } operation) return false;
        _operations.Track(operation.WithoutDonor(shipId)); return true;
    }
    internal WalkAttempt? BeginWalkReturn(Guid id)
    {
        if (!CanMutate || _operations.Get(id) is not { TerminalProgress: DungeonTerminalProgress.Completed, WalkReturn: { Progress: DungeonWalkReturnProgress.Pending } walk } operation) return null;
        var attempted = operation.WithWalkReturn(walk.Begin()); _operations.Track(attempted); _returnDepth++;
        return new(this, RestoreToken, attempted);
    }
    internal sealed class WalkAttempt : IDisposable
    {
        private readonly DungeonPodPersistence _owner; private readonly object _token; private readonly DungeonOperationResumeState _attempted; private bool _disposed;
        internal WalkAttempt(DungeonPodPersistence owner, object token, DungeonOperationResumeState attempted) { _owner = owner; _token = token; _attempted = attempted; }
        internal bool Complete(DungeonPodDeliveryReceipt receipt)
        {
            _owner._hub.CheckThread();
            if (_disposed || !_owner.Ready || !ReferenceEquals(_token, _owner.RestoreToken) || !ReferenceEquals(_owner._operations.Get(_attempted.Id), _attempted) || !_owner._registration!.MutationAllowed || !receipt.AccountsFor(_attempted.WalkReturn!.Crew)) return false;
            _owner._operations.Track(_attempted.WithWalkReturn(_attempted.WalkReturn!.Complete(receipt))); return true;
        }
        public void Dispose() { _owner._hub.CheckThread(); if (_disposed) return; _disposed = true; _owner._returnDepth--; }
    }
    internal RefundAttempt? BeginDockedRefunds(Guid operationId, IReadOnlyList<Guid> ids)
    {
        if (!Ready || !_registration!.MutationAllowed || _hub.IsDispatchingCallbacks || IsCheckpointing || _effects.Uncertain ||
            (!CanMutate && !(_returnDepth == 1 && _terminalOperation == operationId && ReferenceEquals(_terminalToken, RestoreToken)) && !(_returnDepth == 0 && _cancellationOperation == operationId && ReferenceEquals(_cancellationToken, RestoreToken))) || _operations.Get(operationId) is not { } operation) return null;
        var attempts = _ledger.BeginDockedRefunds(ids, operationId, operation.AttackerShipId); if (attempts == null) return null;
        _returnDepth++; return new(this, RestoreToken, attempts);
    }
    internal sealed class RefundAttempt : IDisposable
    {
        private readonly DungeonPodPersistence _owner; private readonly object _token; private readonly IReadOnlyList<DungeonPodResumeState> _attempts; private bool _disposed;
        internal RefundAttempt(DungeonPodPersistence owner, object token, IReadOnlyList<DungeonPodResumeState> attempts) { _owner = owner; _token = token; _attempts = attempts; }
        internal bool Complete(DungeonPodDeliveryReceipt receipt)
        {
            _owner._hub.CheckThread();
            if (_disposed || !_owner.Ready || !ReferenceEquals(_token, _owner.RestoreToken) || !_owner._registration!.MutationAllowed) return false;
            var crew = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var attempt in _attempts)
            {
                if (!ReferenceEquals(_owner._ledger.Get(attempt.Id), attempt)) return false;
                foreach (var pair in attempt.ReturnCrew) { crew.TryGetValue(pair.Key, out var count); crew[pair.Key] = checked(count + pair.Value); }
            }
            if (!receipt.AccountsFor(crew)) return false;
            foreach (var attempt in _attempts) _owner._ledger.Refunded(attempt.Id);
            return true;
        }
        public void Dispose() { _owner._hub.CheckThread(); if (_disposed) return; _disposed = true; _owner._returnDepth--; }
    }
    internal TerminalAttempt? BeginTerminal(Guid id)
    {
        if (!CanMutate || _operations.Get(id) is not { MayStartTerminalEffects: true } operation) return null;
        var attempted = new DungeonOperationResumeState(operation.Id, operation.LocationId, operation.ContentOccurrence, operation.AttackerShipId,
            operation.DungeonType, operation.NativePhase, operation.Outcome, operation.MissionProtection, DungeonTerminalProgress.Attempted, operation.Autonomous, operation.Options, operation.Donors, operation.WalkDispatched, operation.WalkReturn, operation.Retired);
        _operations.Track(attempted); _returnDepth++; _terminalOperation = id; _terminalToken = RestoreToken;
        return new TerminalAttempt(this, RestoreToken, attempted);
    }
    internal sealed class TerminalAttempt : IDisposable
    {
        private readonly DungeonPodPersistence _owner; private readonly object _token; private readonly DungeonOperationResumeState _attempted; private bool _disposed;
        internal TerminalAttempt(DungeonPodPersistence owner, object token, DungeonOperationResumeState attempted) { _owner = owner; _token = token; _attempted = attempted; }
        internal void Completed()
        {
            _owner._hub.CheckThread();
            if (_disposed || !_owner.Ready || !ReferenceEquals(_token, _owner.RestoreToken) || !ReferenceEquals(_owner._operations.Get(_attempted.Id), _attempted)) return;
            _owner._operations.Track(new(_attempted.Id, _attempted.LocationId, _attempted.ContentOccurrence, _attempted.AttackerShipId, _attempted.DungeonType,
                _attempted.NativePhase, _attempted.Outcome, _attempted.MissionProtection, DungeonTerminalProgress.Completed, _attempted.Autonomous, _attempted.Options, _attempted.Donors, _attempted.WalkDispatched, _attempted.WalkReturn, _attempted.Retired));
        }
        public void Dispose() { _owner._hub.CheckThread(); if (_disposed) return; _disposed = true; _owner._returnDepth--; _owner._terminalOperation = null; }
    }
    internal ReturnAttempt? BeginObservedReturn(Guid id, string parentShipId)
    {
        if (!CanMutate) return null;
        var pod = _ledger.Get(id);
        if (pod == null || string.IsNullOrEmpty(pod.ParentShipId) || pod.ParentShipId != parentShipId || _ledger.BeginReturn(id) == null) return null;
        _returnDepth++;
        return new ReturnAttempt(this, _restoredSession, _ledger.Get(id)!);
    }
    internal sealed class ReturnAttempt : IDisposable
    {
        private readonly DungeonPodPersistence _owner;
        private readonly Guid? _session;
        private readonly DungeonPodResumeState _attempted;
        private bool _disposed;
        internal ReturnAttempt(DungeonPodPersistence owner, Guid? session, DungeonPodResumeState attempted)
        { _owner = owner; _session = session; _attempted = attempted; }
        internal bool Complete(DungeonPodDeliveryReceipt receipt)
        {
            _owner._hub.CheckThread();
            if (_disposed || !_owner.Ready || _owner._restoredSession != _session || !ReferenceEquals(_owner._ledger.Get(_attempted.Id), _attempted) ||
                !_owner._registration!.MutationAllowed || !receipt.AccountsFor(_attempted.ReturnCrew)) return false;
            _owner._ledger.Delivered(_attempted.Id); return true;
        }
        public void Dispose()
        { _owner._hub.CheckThread(); if (_disposed) return; _disposed = true; _owner._returnDepth--; }
    }
    internal bool Delivered(Guid id)
    { if (!CanMutate) return false; _ledger.Delivered(id); return true; }
    private byte[] Capture()
    {
        EnsureSerializationAllowed();
        if (_returnDepth != 0 || _disposed || !_restoredSession.HasValue || _restoredSession != _hub.CurrentSession?.Id) throw new InvalidOperationException("Pod state is not restored for this session.");
        return DungeonRecoveryCodec.Encode(_operations.Capture(), _ledger.Capture());
    }
    private void Restore(SessionSnapshot session, byte[]? payload)
    {
        _hub.CheckThread();
        if (_disposed || _hub.CurrentSession?.Id != session.Id) throw new InvalidOperationException("Stale pod restore.");
        var decoded = payload == null ? ((byte[]?)null, (byte[]?)null) : DungeonRecoveryCodec.Decode(payload);
        _operations.Restore(decoded.Item1); _ledger.Restore(decoded.Item2); _restoredSession = session.Id;
        _restoredPods.Clear(); foreach (var pod in _ledger.Snapshot) _restoredPods.Add(pod.Id);
        RestoreToken = new(); _effects.Reset();
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return; _disposed = true;
        _registration?.Dispose(); _lifetime.Dispose(); _restoredSession = null; _ledger.Restore(null); _operations.Restore(null);
    }
}
