using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>Automatic save participation for pod obligations. Consumers supply no serializers.</summary>
internal sealed class DungeonPodPersistence : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly DungeonPodRecoveryLedger _ledger = new();
    private readonly IPersistenceRegistration? _registration;
    private readonly IDisposable _lifetime;
    private Guid? _restoredSession;
    private bool _disposed;
    private int _returnDepth;
    internal DungeonPodPersistence(LifecycleHub hub, IPersistenceApi? persistence)
    {
        _hub = hub;
        _lifetime = hub.Subscribe("vgmodapi.dungeon-pods", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            { _restoredSession = null; _ledger.Restore(null); }
        });
        _registration = persistence?.Register(new PersistenceProvider("vgmodapi.dungeon-pods", 1, Capture, Restore, DungeonPodResumeCodec.Validate));
    }
    internal bool Ready
    {
        get
        {
            _hub.CheckThread(); return !_disposed && _restoredSession.HasValue && _restoredSession == _hub.CurrentSession?.Id &&
                _registration is IPersistenceReadiness readiness && readiness.StateReady;
        }
    }
    internal bool CanMutate => _returnDepth == 0 && Ready && _registration!.MutationAllowed && !_hub.IsDispatchingCallbacks;
    internal IReadOnlyList<DungeonPodResumeState> Snapshot => Ready ? _ledger.Snapshot : Array.Empty<DungeonPodResumeState>();
    internal DungeonPodResumeState? Get(Guid id) => Ready ? _ledger.Get(id) : null;
    internal bool Track(DungeonPodResumeState state)
    { if (!CanMutate) return false; _ledger.Track(state); return true; }
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
    internal bool Delivered(Guid id)
    { if (!CanMutate) return false; _ledger.Delivered(id); return true; }
    private byte[] Capture()
    {
        _hub.CheckThread();
        if (_returnDepth != 0 || _disposed || !_restoredSession.HasValue || _restoredSession != _hub.CurrentSession?.Id) throw new InvalidOperationException("Pod state is not restored for this session.");
        return _ledger.Capture();
    }
    private void Restore(SessionSnapshot session, byte[]? payload)
    {
        _hub.CheckThread();
        if (_disposed || _hub.CurrentSession?.Id != session.Id) throw new InvalidOperationException("Stale pod restore.");
        _ledger.Restore(payload); _restoredSession = session.Id;
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return; _disposed = true;
        _registration?.Dispose(); _lifetime.Dispose(); _restoredSession = null; _ledger.Restore(null);
    }
}
