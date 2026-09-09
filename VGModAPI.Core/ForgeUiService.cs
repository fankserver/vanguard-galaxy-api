using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal interface IForgeUiSource
{
    ForgeSelectionSnapshot? ReadUi(Guid session);
    ForgeNavigationStatus OpenUi(Guid session, RecipeId recipe);
    void ClearUi();
}

internal sealed class ForgeUiService : IForgeUiService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IForgeUiSource? _source;
    private readonly IServiceStatus _status;
    private readonly ServiceSubscriptions<ForgeSelectionChange> _handlers;
    private readonly Action<string, Exception> _report;
    private readonly IDisposable _lifetime;
    private readonly List<Subscription> _subscribers = new();
    private readonly List<Registration> _actions = new();
    private readonly Queue<ForgeSelectionChange> _events = new();
    private ForgeSelectionSnapshot? _current;
    private bool _closing, _disposed, _refreshing, _dispatching, _navigating;
    private long _revision;
    internal bool IsDispatchingCallbacks => _dispatching;
    internal ForgeUiService(LifecycleHub hub, IForgeUiSource? source, Action<string, Exception> report)
    {
        _hub = hub; _source = source; _report = report;
        _status = hub.Services.Get("forge-ui");
        if (source == null && Availability.IsAvailable) hub.SetCapability("forge-ui", false, "Forge UI bindings unavailable.");
        _handlers = new ServiceSubscriptions<ForgeSelectionChange>(hub, Subscribe,
            change => change.Current == null || Availability.IsAvailable && ReferenceEquals(change.Current, _current));
        _lifetime = hub.Subscribe("vgmodapi.forge-ui", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            { _source?.ClearUi(); Change(null); }
        });
    }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public event Action<ForgeSelectionChange>? Changed { add => _handlers.Add(value); remove => _handlers.Remove(value); }
    internal void SetAvailable(bool available)
    {
        _hub.CheckThread(); if (_disposed || _closing) return;
        available &= _source != null;
        _hub.SetCapability("forge-ui", available, available ? "Experimental scoped Forge UI." : "Forge UI unavailable.");
        if (!available && !_disposed) { _source?.ClearUi(); Change(null); }
    }
    public ForgeSelectionSnapshot? Current { get { Refresh(); return _current; } }
    internal void Refresh()
    {
        _hub.CheckThread(); if (_refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            var session = _hub.CurrentSession;
            if (_closing || _source == null || !Availability.IsAvailable || session?.Phase != SessionPhase.GameplayInitialized) { _source?.ClearUi(); Change(null); return; }
            var value = _source.ReadUi(session.Id);
            if (_disposed || !Availability.IsAvailable || _hub.CurrentSession?.Id != session.Id || _hub.CurrentSession.Phase != SessionPhase.GameplayInitialized) { Change(null); return; }
            if (value != null && value.View.SessionId != session.Id) throw new InvalidOperationException("Foreign UI session.");
            if (Same(_current, value)) return;
            Change(value == null ? null : new ForgeSelectionSnapshot(value.View, value.Station, value.ParentRecipe, value.SelectedRecipe,
                value.AvailableVariants, value.Batches, checked(++_revision), value.Presentation));
        }
        catch (Exception error) { Report("vgmodapi.forge-ui", error); SetAvailable(false); }
        finally { _refreshing = false; }
    }
    private static bool Same(ForgeSelectionSnapshot? a, ForgeSelectionSnapshot? b) => ReferenceEquals(a, b) || a != null && b != null &&
        a.View.Equals(b.View) && a.Station.Equals(b.Station) && a.ParentRecipe.Equals(b.ParentRecipe) && a.SelectedRecipe.Equals(b.SelectedRecipe) &&
        a.Batches == b.Batches && a.AvailableVariants.SequenceEqual(b.AvailableVariants) && a.Presentation.DisplayName == b.Presentation.DisplayName &&
        a.Presentation.HasNativeIcon == b.Presentation.HasNativeIcon;
    private void Change(ForgeSelectionSnapshot? next)
    {
        if (ReferenceEquals(next, _current)) return;
        var change = new ForgeSelectionChange(_current, next); _current = next; _events.Enqueue(change);
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            while (_events.Count != 0)
            {
                var item = _events.Dequeue();
                foreach (var subscriber in _subscribers.ToArray())
                {
                    if (_disposed) break;
                    if (item.Current != null && !ReferenceEquals(item.Current, _current)) break;
                    if (subscriber.Disposed) continue;
                    try { subscriber.Callback(item); } catch (Exception error) { Report(subscriber.Plugin, error); }
                }
            }
        }
        finally { _dispatching = false; }
    }
    internal IDisposable Subscribe(string pluginId, Action<ForgeSelectionChange> callback)
    {
        _hub.CheckThread(); ThrowDisposed(); pluginId = Identity(pluginId);
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        if (_subscribers.Count >= 128) throw new InvalidOperationException("Forge subscription limit reached.");
        var entry = new Subscription(this, pluginId, callback); _subscribers.Add(entry); return entry;
    }
    public IForgeActionRegistration RegisterAction(string pluginId, string localId, ForgeActionPresentation presentation,
        Action<ForgeSelectionSnapshot> callback, int order = 0)
    {
        _hub.CheckThread(); ThrowDisposed(); pluginId = Identity(pluginId); localId = Identity(localId);
        if (presentation == null || callback == null) throw new ArgumentNullException(presentation == null ? nameof(presentation) : nameof(callback));
        if (_actions.Any(action => action.Plugin == pluginId && action.Local == localId)) throw new InvalidOperationException("Duplicate provider action identity.");
        if (_actions.Count >= 16) throw new InvalidOperationException("Forge action limit reached.");
        var entry = new Registration(this, pluginId, localId, presentation, callback, order); _actions.Add(entry); return entry;
    }
    internal IReadOnlyList<Registration> Actions
    {
        get { _hub.CheckThread(); return _actions.OrderBy(action => action.Order).ThenBy(action => action.Plugin, StringComparer.Ordinal).ThenBy(action => action.Local, StringComparer.Ordinal).ToArray(); }
    }
    internal bool Invoke(Guid registration, ForgeViewHandle view, long revision)
    {
        _hub.CheckThread(); Refresh();
        if (_disposed || _closing || !Availability.IsAvailable || _dispatching || _navigating || _current == null || !_current.View.Equals(view) || _current.Revision != revision) return false;
        var action = _actions.SingleOrDefault(item => item.Token == registration);
        if (action == null || !action.Presentation.Enabled) return false;
        try { action.Callback(_current); return true; } catch (Exception error) { Report(action.Plugin, error); return false; }
    }
    public ForgeNavigationStatus Open(RecipeId recipe)
    {
        _hub.CheckThread(); if (recipe == null) throw new ArgumentNullException(nameof(recipe));
        if (_source == null || !Availability.IsAvailable || _disposed || _closing) return ForgeNavigationStatus.Unavailable;
        if (_dispatching || _navigating || _hub.IsDispatchingCallbacks) return ForgeNavigationStatus.Busy;
        var session = _hub.CurrentSession;
        if (session?.Phase != SessionPhase.GameplayInitialized) return ForgeNavigationStatus.NotAtStation;
        _navigating = true;
        try
        {
            var result = _source.OpenUi(session.Id, recipe); Refresh();
            if (_disposed || !Availability.IsAvailable || _hub.CurrentSession?.Id != session.Id) return ForgeNavigationStatus.Uncertain;
            return result == ForgeNavigationStatus.Selected && (_current == null || !_current.SelectedRecipe.Equals(recipe)) ? ForgeNavigationStatus.Uncertain : result;
        }
        catch (Exception error) { Report("vgmodapi.forge-ui", error); return ForgeNavigationStatus.Uncertain; }
        finally { _navigating = false; }
    }
    private static string Identity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)) throw new ArgumentException("A bounded identity without control characters is required.");
        return value;
    }
    private void ThrowDisposed() { if (_disposed || _closing) throw new ObjectDisposedException(nameof(ForgeUiService)); }
    private void Report(string plugin, Exception error) { try { _report(plugin, error); } catch { } }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed || _closing) return;
        _closing = true;
        if (Availability.IsAvailable) _hub.SetCapability("forge-ui", false, "Forge UI service stopped.", ServiceUnavailableReason.ApiStopped);
        _source?.ClearUi(); Change(null); _disposed = true; _lifetime.Dispose(); _handlers.Dispose();
        foreach (var action in _actions.ToArray()) action.Dispose();
        foreach (var subscriber in _subscribers.ToArray()) subscriber.Dispose();
        _events.Clear();
    }
    private sealed class Subscription : IDisposable
    {
        private readonly ForgeUiService _owner;
        internal readonly string Plugin;
        internal readonly Action<ForgeSelectionChange> Callback;
        internal bool Disposed;
        internal Subscription(ForgeUiService owner, string plugin, Action<ForgeSelectionChange> callback) { _owner = owner; Plugin = plugin; Callback = callback; }
        public void Dispose() { _owner._hub.CheckThread(); Disposed = true; _owner._subscribers.Remove(this); }
    }
    internal sealed class Registration : IForgeActionRegistration
    {
        private readonly ForgeUiService _owner;
        internal readonly Guid Token = Guid.NewGuid();
        internal readonly string Plugin, Local;
        internal readonly int Order;
        internal readonly Action<ForgeSelectionSnapshot> Callback;
        internal ForgeActionPresentation Presentation { get; private set; }
        private bool _disposed;
        internal Registration(ForgeUiService owner, string plugin, string local, ForgeActionPresentation presentation, Action<ForgeSelectionSnapshot> callback, int order)
        { _owner = owner; Plugin = plugin; Local = local; Presentation = presentation; Callback = callback; Order = order; }
        public void Update(ForgeActionPresentation presentation)
        { _owner._hub.CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(Registration)); Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation)); }
        public void Dispose() { _owner._hub.CheckThread(); _disposed = true; _owner._actions.Remove(this); }
    }
}
