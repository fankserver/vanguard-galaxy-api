using System;
using VGModAPI;

namespace UiSurfaces;

/// <summary>Custom per-save data. The API handles save outcomes and restoration; no separate sidecar or slot tracking.</summary>
internal sealed class WindowVisits : IDisposable
{
    private readonly ISaveDataRegistration _registration;
    private int _count;
    private bool _disposed;
    internal event Action? Changed;
    internal int? Count => !_disposed && _registration.CanRead ? _count : null;
    internal SaveDataState State => _registration.State;

    internal WindowVisits(ISaveDataService saveData, string pluginId)
    {
        var result = saveData.Register(new PersistenceProvider(pluginId, 1, Capture, Restore, Validate));
        _registration = result.Registration ?? throw new InvalidOperationException("Window visits registration refused: " + result.Status + ". " + result.Detail);
        _registration.StateChanged += OnState;
    }

    internal bool RecordOpen()
    {
        if (_disposed || !_registration.CanMutate || _count == int.MaxValue) return false;
        _count++;
        Changed?.Invoke();
        return true;
    }

    // Version 1 is one nonnegative 32-bit integer in little-endian order.
    private byte[] Capture() => new[] { (byte)_count, (byte)(_count >> 8), (byte)(_count >> 16), (byte)(_count >> 24) };
    private static bool Validate(byte[] data) => data != null && data.Length == 4 && (data[3] & 128) == 0;
    private void Restore(SessionSnapshot session, byte[]? data)
    {
        if (data == null) { _count = 0; return; } // A new game starts independently.
        if (!Validate(data)) throw new ArgumentException("Invalid window-visit payload.", nameof(data));
        _count = data[0] | data[1] << 8 | data[2] << 16 | data[3] << 24;
    }
    private void OnState(SaveDataState state) => Changed?.Invoke();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration.StateChanged -= OnState;
        _registration.Dispose();
        Changed = null;
    }
}
