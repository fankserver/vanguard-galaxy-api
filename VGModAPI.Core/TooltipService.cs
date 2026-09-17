using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

internal sealed class TooltipService : ITooltipService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly List<Entry> _entries = new();
    private bool _disposed, _rendering;
    internal TooltipService(LifecycleHub hub) { _hub = hub; _status = hub.Services.Get("tooltips"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    internal void SetAvailable(bool available)
    {
        _hub.CheckThread(); if (_disposed) return;
        if (available) _hub.SetAvailable("tooltips", "Ship module, item and skill-tree tooltip extensions bound.");
        else _hub.SetUnavailable("tooltips", ServiceUnavailableReason.BindingFailed, "Tooltip extensions unavailable.");
    }
    public IDisposable RegisterShipModule(string pluginId, Action<ShipModule, Tooltip> describe)
    {
        if (describe == null) throw new ArgumentNullException(nameof(describe));
        return Register(pluginId, typeof(ShipModule), (value, tooltip) => describe((ShipModule)value, tooltip));
    }
    public IDisposable RegisterItem(string pluginId, Action<ItemInfo, Tooltip> describe)
    {
        if (describe == null) throw new ArgumentNullException(nameof(describe));
        return Register(pluginId, typeof(ItemInfo), (value, tooltip) => describe((ItemInfo)value, tooltip));
    }
    public IDisposable RegisterSkillTree(string pluginId, Action<SkillTree, Tooltip> describe)
    {
        if (describe == null) throw new ArgumentNullException(nameof(describe));
        return Register(pluginId, typeof(SkillTree), (value, tooltip) => describe((SkillTree)value, tooltip));
    }
    private IDisposable Register(string pluginId, Type type, Action<object, Tooltip> describe)
    {
        _hub.CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(TooltipService));
        var id = new RecipeId(pluginId, "tooltip").ProviderId;
        if (_entries.Exists(e => e.Owner == id && e.Type == type)) throw new InvalidOperationException("Duplicate tooltip owner and target.");
        var entry = new Entry(this, id, type, describe); _entries.Add(entry); return entry;
    }
    internal IReadOnlyList<TooltipLine> Describe(object value)
    {
        _hub.CheckThread(); var lines = new List<TooltipLine>();
        if (_disposed || _rendering || !Availability.IsAvailable) return lines;
        _rendering = true;
        try
        {
            using var scope = _hub.EnterServiceDispatch();
            foreach (var entry in _entries.ToArray())
            {
                if (entry.Callback == null || entry.Type != value.GetType()) continue;
                var content = new Tooltip(_hub.CheckThread);
                try
                {
                    entry.Callback(value, content);
                    if (_disposed || !Availability.IsAvailable) return Array.Empty<TooltipLine>();
                    if (entry.Callback != null) lines.AddRange(content.Lines);
                }
                catch (Exception error) { _hub.ReportSubscriberFailure(entry.Owner, error); }
                finally { content.Close(); }
            }
            return lines;
        }
        finally { _rendering = false; }
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.ToArray()) entry.Dispose();
        _hub.SetUnavailable("tooltips", ServiceUnavailableReason.ApiStopped, "Tooltip extensions stopped.");
    }
    private sealed class Entry : IDisposable
    {
        private readonly TooltipService _service;
        internal readonly string Owner;
        internal readonly Type Type;
        internal Action<object, Tooltip>? Callback;
        internal Entry(TooltipService service, string owner, Type type, Action<object, Tooltip> callback)
        { _service = service; Owner = owner; Type = type; Callback = callback; }
        public void Dispose() { _service._hub.CheckThread(); Callback = null; _service._entries.Remove(this); }
    }
}
