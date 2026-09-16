using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

internal sealed class EquipmentService : IEquipmentService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly List<Entry> _entries = new();
    private bool _disposed, _evaluating;
    internal EquipmentService(LifecycleHub hub) { _hub = hub; _status = hub.Services.Get("equipment"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    internal void SetAvailable(bool available)
    {
        _hub.CheckThread(); if (_disposed) return;
        if (available) _hub.SetAvailable("equipment", "Player tractor module targeting bound.");
        else _hub.SetUnavailable("equipment", ServiceUnavailableReason.BindingFailed, "Equipment integration unavailable.");
    }
    public IDisposable ConfigurePlayerTractorModules(string pluginId, Func<TractorModule, TractorTargeting?> configure)
    {
        _hub.CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(EquipmentService));
        var id = new RecipeId(pluginId, "tractor").ProviderId;
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        if (_entries.Exists(e => e.Owner == id)) throw new InvalidOperationException("Duplicate tractor rule owner.");
        var entry = new Entry(this, id, configure); _entries.Add(entry); return entry;
    }
    internal TractorTargeting? Resolve(TractorModule module)
    {
        _hub.CheckThread();
        if (_disposed || _evaluating || !Availability.IsAvailable) return null;
        _evaluating = true;
        try
        {
            using var scope = _hub.EnterServiceDispatch();
            foreach (var entry in _entries.ToArray())
            {
                if (entry.Configure == null) continue;
                try
                {
                    var result = entry.Configure(module);
                    if (_disposed || !Availability.IsAvailable) return null;
                    if (entry.Configure != null && result != null) return result;
                }
                catch (Exception error) { _hub.ReportSubscriberFailure(entry.Owner, error); }
            }
            return null;
        }
        finally { _evaluating = false; }
    }
    // Native automatic targeting is not reduced: configure sharing without breaking vanilla's own pool.
    internal static int AutomaticCapacity(TractorModule module, TractorTargeting targeting)
        => Math.Max(module.BeamCount, Math.Min(checked(module.BeamCount + module.ManualBeamCount), targeting.AutomaticBeamLimit));
    internal static bool MayBorrow(TractorModule module, TractorTargeting targeting, int busyBeams, bool manual)
        => manual ? targeting.AllowManualBorrowing : busyBeams < AutomaticCapacity(module, targeting);
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.ToArray()) entry.Dispose();
        _hub.SetUnavailable("equipment", ServiceUnavailableReason.ApiStopped, "Equipment integration stopped.");
    }
    private sealed class Entry : IDisposable
    {
        private readonly EquipmentService _service;
        internal readonly string Owner;
        internal Func<TractorModule, TractorTargeting?>? Configure;
        internal Entry(EquipmentService service, string owner, Func<TractorModule, TractorTargeting?> configure)
        { _service = service; Owner = owner; Configure = configure; }
        public void Dispose() { _service._hub.CheckThread(); Configure = null; _service._entries.Remove(this); }
    }
}
