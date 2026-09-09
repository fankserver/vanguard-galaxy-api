using System;

namespace VGModAPI.Examples;

/// <summary>Additional custom mod data, not API-managed story or dungeon state.</summary>
public sealed class CustomCounter : IDisposable
{
    private readonly ISaveDataRegistration _registration;
    private readonly Action<SaveDataState> _report;
    private int _value;
    private bool _disposed;

    public CustomCounter(ISaveDataService saveData, string pluginId, Action<SaveDataState> report)
    {
        if (saveData == null) throw new ArgumentNullException(nameof(saveData));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        var result = saveData.Register(new PersistenceProvider(pluginId, 1, Capture, Restore, Validate));
        _registration = result.Registration ?? throw new InvalidOperationException("Save-data registration refused: " + result.Status + ". " + result.Detail);
        try
        {
            _registration.StateChanged += OnState;
            OnState(_registration.State);
        }
        catch { Dispose(); throw; }
    }

    public bool TryRead(out int value)
    {
        value = 0;
        if (_disposed || !_registration.CanRead) return false;
        value = _value;
        return true;
    }

    public bool TryIncrement()
    {
        if (_disposed || !_registration.CanMutate || _value == int.MaxValue) return false;
        ++_value;
        return true;
    }

    private byte[] Capture() => new[] { (byte)_value, (byte)(_value >> 8), (byte)(_value >> 16), (byte)(_value >> 24) };
    private static bool Validate(byte[] payload) => payload != null && payload.Length == 4 && (payload[3] & 128) == 0;
    private void Restore(SessionSnapshot session, byte[]? payload)
    {
        if (payload == null) { _value = 0; return; }
        if (!Validate(payload)) throw new ArgumentException("Invalid counter payload.", nameof(payload));
        _value = payload[0] | payload[1] << 8 | payload[2] << 16 | payload[3] << 24;
    }

    private void OnState(SaveDataState state)
    {
        if (!_disposed) _report(state);
        // Keep the registration across readiness changes. Re-registering here could block other mods' saves.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration.StateChanged -= OnState;
        _registration.Dispose();
    }
}
