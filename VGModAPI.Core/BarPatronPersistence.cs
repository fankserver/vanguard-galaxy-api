using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>API-owned persistence wiring; patron authors do not supply storage callbacks.</summary>
internal sealed class BarPatronPersistence : IDisposable
{
    private readonly BarPatronLedger _ledger = new();
    private readonly ILifecycleService _lifecycle;
    private readonly ISaveDataRegistration _registration;
    private readonly Action _checkThread;
    private Guid? _restored;
    private bool _disposed;

    internal BarPatronPersistence(ISaveDataService persistence, ILifecycleService lifecycle, Action checkThread)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _checkThread = checkThread ?? throw new ArgumentNullException(nameof(checkThread));
        _checkThread();
        _registration = (persistence ?? throw new ArgumentNullException(nameof(persistence))).Register(new PersistenceProvider(
            BarPatronCodec.Owner, BarPatronCodec.SchemaVersion, Capture, Restore, BarPatronCodec.Validate,
            migrations: new Dictionary<int, Func<byte[], byte[]>> { [1] = bytes => bytes })).Registration ?? throw new InvalidOperationException("Patron save provider registration refused.");
        try { lifecycle.Changed += OnLifecycle; }
        catch { _registration.Dispose(); throw; }
    }

    internal ISaveDataRegistration Registration => _registration;

    internal bool Read(Guid session, out IReadOnlyList<BarPatronState> rows)
    {
        _checkThread();
        rows = Array.Empty<BarPatronState>();
        if (!Ready(session)) return false;
        rows = _ledger.Snapshot();
        return true;
    }

    internal bool CanMutate(Guid session)
    {
        _checkThread();
        return Ready(session) && _registration.CanMutate;
    }

    // Only the owning service passes authenticated provider identities after checking its leases.
    internal bool Put(Guid session, string authenticatedProvider, BarPatronState state)
    {
        _checkThread();
        return Ready(session) && _registration.CanMutate && _ledger.TryPut(authenticatedProvider, state);
    }
    internal bool Remove(Guid session, string authenticatedProvider, BarPatronId id)
    {
        _checkThread();
        return Ready(session) && _registration.CanMutate && _ledger.TryRemove(authenticatedProvider, id);
    }
    private bool Ready(Guid session) => !_disposed && _restored == session && _lifecycle.CurrentSession?.Id == session
        && _lifecycle.CurrentSession.Phase == SessionPhase.GameplayInitialized
        && _registration?.CanRead == true;

    private byte[] Capture()
    {
        _checkThread();
        if (_disposed || !_restored.HasValue || _lifecycle.CurrentSession?.Id != _restored) throw new InvalidOperationException("Patron state is not restored for this session.");
        return _ledger.Capture();
    }
    private void Restore(SessionSnapshot session, byte[]? payload)
    {
        _checkThread();
        if (_disposed || _lifecycle.CurrentSession?.Id != session.Id) throw new InvalidOperationException("Stale patron restoration.");
        _restored = null;
        if (payload == null) _ledger.Reset();
        else _ledger.Restore(payload);
        _restored = session.Id;
    }
    private void OnLifecycle(LifecycleEvent value)
    {
        _checkThread();
        var current = _lifecycle.CurrentSession;
        if (_disposed || value.Session == null || current?.Id != value.Session.Id || current.Phase != value.Session.Phase) return;
        if (value.Kind != LifecycleEventKind.SessionStarting && value.Kind != LifecycleEventKind.SessionInvalidated
            && value.Kind != LifecycleEventKind.SessionStartFailed) return;
        _restored = null;
        _ledger.Reset();
    }
    public void Dispose()
    {
        _checkThread();
        if (_disposed) return;
        _disposed = true;
        _restored = null;
        _ledger.Reset();
        _lifecycle.Changed -= OnLifecycle;
        _registration.Dispose();
    }
}
