using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

internal sealed class PickupPresentationService : IPickupPresentationService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly List<Entry> _entries = new();
    private bool _disposed, _resolving;
    internal PickupPresentationService(LifecycleHub hub)
    { _hub = hub; _status = hub.Services.Get("pickup-presentation"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    internal void SetAvailable(bool available)
    {
        _hub.CheckThread();
        if (_disposed) return;
        if (available) _hub.SetAvailable("pickup-presentation", "Item pickup presentation bound.");
        else _hub.SetUnavailable("pickup-presentation", ServiceUnavailableReason.BindingFailed, "Item pickup presentation unavailable.");
    }
    public IDisposable Register(string pluginId, Func<ItemPickupPresentation, UiColor?> resolveColor)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(PickupPresentationService));
        var identity = new RecipeId(pluginId, "pickup-color");
        if (resolveColor == null) throw new ArgumentNullException(nameof(resolveColor));
        if (_entries.Exists(entry => entry.Owner == identity.ProviderId))
            throw new InvalidOperationException("A pickup resolver is already registered for this plugin.");
        var entry = new Entry(this, identity.ProviderId, resolveColor);
        _entries.Add(entry);
        return entry;
    }
    internal UiColor? Resolve(ItemPickupPresentation pickup)
    {
        _hub.CheckThread();
        if (_disposed || _resolving || !Availability.IsAvailable) return null;
        _resolving = true;
        try
        {
            using var dispatch = _hub.EnterServiceDispatch();
            foreach (var entry in _entries.ToArray())
            {
                if (entry.Callback == null) continue;
                try
                {
                    var color = entry.Callback(pickup);
                    if (_disposed || !Availability.IsAvailable) return null;
                    if (entry.Callback != null && color.HasValue) return color;
                }
                catch (Exception error) { _hub.ReportSubscriberFailure(entry.Owner, error); }
            }
            return null;
        }
        finally { _resolving = false; }
    }
    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.ToArray()) entry.Dispose();
        _hub.SetUnavailable("pickup-presentation", ServiceUnavailableReason.ApiStopped, "Pickup presentation stopped.");
    }
    private sealed class Entry : IDisposable
    {
        private readonly PickupPresentationService _service;
        internal readonly string Owner;
        internal Func<ItemPickupPresentation, UiColor?>? Callback;
        internal Entry(PickupPresentationService service, string owner, Func<ItemPickupPresentation, UiColor?> callback)
        { _service = service; Owner = owner; Callback = callback; }
        public void Dispose()
        { _service._hub.CheckThread(); Callback = null; _service._entries.Remove(this); }
    }
}
