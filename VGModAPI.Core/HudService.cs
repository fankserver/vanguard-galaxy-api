using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class HudService : IHudService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly Action<string, Exception> _report;
    private readonly IDisposable _lifetime;
    private readonly List<Entry> _entries = new();
    private Guid? _surface, _session;
    private bool _disposed, _invoking;
    internal Func<bool>? SurfaceLive { get; set; }
    internal HudService(LifecycleHub hub, Action<string, Exception> report)
    {
        _hub = hub; _report = report;
        _status = hub.Services.Get("hud");
        _lifetime = hub.Subscribe("vgmodapi.hud", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
                SetSurface(null, null);
        });
    }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    internal void SetAvailable(bool value)
    {
        _hub.CheckThread(); if (_disposed) return;
        _hub.SetCapability("hud", value, value ? "Experimental shared HUD presentation." : "Shared HUD unavailable.");
        if (!value) SetSurface(null, null);
    }
    internal void SetSurface(Guid? surface, Guid? session)
    {
        _hub.CheckThread();
        if (_disposed || !Availability.IsAvailable || surface == Guid.Empty || session == Guid.Empty || session != _hub.CurrentSession?.Id || _hub.CurrentSession?.Phase != SessionPhase.GameplayInitialized)
        { _surface = null; _session = null; return; }
        _surface = surface; _session = surface == null ? null : session;
    }
    public bool Visible { get { _hub.CheckThread(); return !_disposed && Availability.IsAvailable && _surface != null && _session == _hub.CurrentSession?.Id && _hub.CurrentSession?.Phase == SessionPhase.GameplayInitialized && (SurfaceLive?.Invoke() ?? true); } }
    internal Guid? Surface => _surface;
    internal IReadOnlyList<Entry> Entries
    { get { _hub.CheckThread(); return _entries.OrderBy(entry => entry.Order).ThenBy(entry => entry.Plugin, StringComparer.Ordinal).ThenBy(entry => entry.Local, StringComparer.Ordinal).ToArray(); } }
    internal IReadOnlyList<Entry> Launchers(HudCorner corner) => Entries
        .Where(entry => entry.Panel == null && entry.Button?.Corner == corner).ToArray();
    public IHudRegistration Register(string pluginId, string localId, Action<HudInteraction> callback, int order = 0)
    {
        _hub.CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(HudService));
        var identity = new RecipeId(pluginId, localId);
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        if (_entries.Count >= 16) throw new InvalidOperationException("HUD registration limit reached.");
        if (_entries.Any(entry => entry.Plugin == identity.ProviderId && entry.Local == identity.LocalId)) throw new InvalidOperationException("Duplicate provider HUD identity.");
        var result = new Entry(this, identity.ProviderId, identity.LocalId, callback, order); _entries.Add(result); return result;
    }
    internal bool Invoke(Guid token, Guid surface, long revision, HudInteractionKind kind, string? rowId = null)
    {
        _hub.CheckThread();
        if (!Visible || _surface != surface || _invoking || _hub.IsDispatchingCallbacks) return false;
        var entry = _entries.SingleOrDefault(value => value.Token == token);
        if (entry == null || entry.Revision != revision) return false;
        var valid = kind switch
        {
            HudInteractionKind.Button => rowId == null && entry.Button?.Enabled == true,
            HudInteractionKind.ClosePanel => rowId == null && entry.Panel?.Closable == true,
            HudInteractionKind.Row => rowId != null && entry.Panel?.Rows.Any(row => row.Id == rowId && row.Clickable) == true,
            _ => false
        };
        if (!valid) return false;
        _invoking = true;
        try { entry.Callback!(new HudInteraction(_session!.Value, kind, rowId, revision)); return true; }
        catch (Exception error) { try { _report(entry.Plugin, error); } catch { } return false; }
        finally { _invoking = false; }
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; _surface = null; _session = null; SurfaceLive = null; _lifetime.Dispose();
        if (Availability.IsAvailable) _hub.SetCapability("hud", false, "HUD service stopped.", ServiceUnavailableReason.ApiStopped);
        foreach (var entry in _entries.ToArray()) entry.Dispose();
    }
    internal sealed class Entry : IHudRegistration
    {
        private readonly HudService _owner;
        internal readonly Guid Token = Guid.NewGuid();
        internal readonly string Plugin, Local;
        internal readonly int Order;
        internal Action<HudInteraction>? Callback;
        internal HudButton? Button { get; private set; }
        internal HudPanel? Panel { get; private set; }
        internal long Revision { get; private set; }
        private bool _disposed;
        internal Entry(HudService owner, string plugin, string local, Action<HudInteraction> callback, int order)
        { _owner = owner; Plugin = plugin; Local = local; Callback = callback; Order = order; }
        public void Update(HudButton? button, HudPanel? panel)
        {
            _owner._hub.CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(Entry));
            if (panel != null && Panel == null && _owner._entries.Count(entry => entry.Panel != null) >= 4) throw new InvalidOperationException("HUD panel limit reached.");
            if (ReferenceEquals(button, Button) && ReferenceEquals(panel, Panel)) return;
            var revision = checked(Revision + 1); Button = button; Panel = panel; Revision = revision;
        }
        public void Dispose()
        {
            _owner._hub.CheckThread(); if (_disposed) return;
            _disposed = true; Button = null; Panel = null; Callback = null; _owner._entries.Remove(this);
        }
    }
}
