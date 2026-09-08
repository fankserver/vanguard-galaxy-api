using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal interface IDungeonPanelSource
{
    DungeonPanelCapabilities Capabilities { get; }
    DungeonPanelSnapshot? Read();
    DungeonPanelOpenStatus Open(BoardingHandle target);
}

internal sealed class DungeonPanelService : IDungeonPanelApi, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IDungeonPanelSource _source;
    private readonly Action<string, Exception> _report;
    private readonly List<Registration> _registrations = new();
    private bool _disposed, _dispatching, _navigating;
    internal DungeonPanelService(LifecycleHub hub, IDungeonPanelSource source, Action<string, Exception> report)
    { _hub = hub; _source = source; _report = report; }
    public DungeonPanelCapabilities Capabilities { get { _hub.CheckThread(); return _disposed ? new(false, false, false) : _source.Capabilities; } }
    public DungeonPanelSnapshot? Current
    {
        get
        {
            _hub.CheckThread(); if (_disposed) return null;
            var snapshot = _source.Read(); var session = _hub.CurrentSession;
            return session?.Phase == SessionPhase.GameplayInitialized && snapshot?.Target.Handle.SessionId == session.Id ? snapshot : null;
        }
    }
    public DungeonPanelOpenStatus Open(BoardingHandle target)
    {
        _hub.CheckThread(); if (target == null) throw new ArgumentNullException(nameof(target));
        if (_disposed || !Capabilities.Opening) return DungeonPanelOpenStatus.Unavailable;
        if (_dispatching || _navigating || _hub.IsDispatchingCallbacks) return DungeonPanelOpenStatus.Busy;
        if (_hub.CurrentSession?.Id != target.SessionId || _hub.CurrentSession.Phase != SessionPhase.GameplayInitialized) return DungeonPanelOpenStatus.StaleTarget;
        _navigating = true;
        try { return _source.Open(target); }
        finally { _navigating = false; }
    }
    public IDisposable RegisterSection(string pluginId, string localId, Func<DungeonPanelSnapshot, DungeonPanelSection?> present, int order = 0)
        => Register(pluginId, localId, present ?? throw new ArgumentNullException(nameof(present)), null, null, order);
    public IDisposable RegisterAction(string pluginId, string localId, Func<DungeonPanelSnapshot, DungeonPanelAction?> present, Action<DungeonPanelSnapshot> activate, int order = 0)
        => Register(pluginId, localId, null, present ?? throw new ArgumentNullException(nameof(present)), activate ?? throw new ArgumentNullException(nameof(activate)), order);
    private Registration Register(string pluginId, string localId, Func<DungeonPanelSnapshot, DungeonPanelSection?>? section, Func<DungeonPanelSnapshot, DungeonPanelAction?>? action, Action<DungeonPanelSnapshot>? activate, int order)
    {
        _hub.CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(DungeonPanelService));
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > 128 || string.IsNullOrWhiteSpace(localId) || localId.Length > 128) throw new ArgumentException("Invalid contribution identity.");
        if (_registrations.Count >= 2048 || _registrations.Any(item => item.Plugin == pluginId && item.Local == localId)) throw new InvalidOperationException("Duplicate contribution or panel capacity exceeded.");
        var registration = new Registration(this, pluginId, localId, section, action, activate, order); _registrations.Add(registration); return registration;
    }
    internal IReadOnlyList<Row> Render()
    {
        _hub.CheckThread(); var snapshot = Current; var rows = new List<Row>(); if (snapshot == null || _dispatching || _navigating) return rows;
        _dispatching = true;
        try
        {
            foreach (var item in _registrations.OrderBy(value => value.Order).ThenBy(value => value.Plugin, StringComparer.Ordinal).ThenBy(value => value.Local, StringComparer.Ordinal).ToArray())
            {
                if (!_registrations.Contains(item)) continue;
                try
                {
                    var section = Capabilities.StatusSections ? item.Section?.Invoke(snapshot) : null;
                    var action = Capabilities.ContextualActions ? item.Action?.Invoke(snapshot) : null;
                    if (_registrations.Contains(item) && Matches(snapshot, Current) && (section != null || action != null)) rows.Add(new(item.Id, snapshot, section, action));
                }
                catch (Exception error) { Report(item.Plugin, error); }
            }
        }
        finally { _dispatching = false; }
        return Matches(snapshot, Current) ? rows.AsReadOnly() : Array.Empty<Row>();
    }
    internal bool Activate(Guid registrationId, Guid viewId, long revision)
    {
        _hub.CheckThread(); if (_dispatching || _navigating || _hub.IsDispatchingCallbacks || !Capabilities.ContextualActions) return false;
        var snapshot = Current; if (snapshot == null || snapshot.ViewId != viewId || snapshot.Revision != revision) return false;
        var item = _registrations.FirstOrDefault(value => value.Id == registrationId); if (item?.Action == null) return false;
        _dispatching = true;
        try
        {
            if (item.Action(snapshot)?.Enabled != true || !_registrations.Contains(item) || !Matches(snapshot, Current)) return false;
            item.Activate!(snapshot); return true;
        }
        catch (Exception error) { Report(item.Plugin, error); return false; }
        finally { _dispatching = false; }
    }
    private static bool Matches(DungeonPanelSnapshot expected, DungeonPanelSnapshot? current) => current != null && expected.ViewId == current.ViewId && expected.Revision == current.Revision && expected.Target.Handle.Equals(current.Target.Handle) && expected.Target.Revision == current.Target.Revision && expected.Operation?.Revision == current.Operation?.Revision && Equals(expected.Operation?.Handle, current.Operation?.Handle);
    private void Report(string plugin, Exception error) { try { _report(plugin, error); } catch { } }
    public void Dispose() { _hub.CheckThread(); _disposed = true; _registrations.Clear(); }
    internal sealed class Row
    {
        internal readonly Guid Registration; internal readonly DungeonPanelSnapshot Snapshot; internal readonly DungeonPanelSection? Section; internal readonly DungeonPanelAction? Action;
        internal Row(Guid registration, DungeonPanelSnapshot snapshot, DungeonPanelSection? section, DungeonPanelAction? action) { Registration = registration; Snapshot = snapshot; Section = section; Action = action; }
    }
    private sealed class Registration : IDisposable
    {
        private readonly DungeonPanelService _owner;
        internal readonly Guid Id = Guid.NewGuid(); internal readonly string Plugin, Local; internal readonly int Order;
        internal readonly Func<DungeonPanelSnapshot, DungeonPanelSection?>? Section; internal readonly Func<DungeonPanelSnapshot, DungeonPanelAction?>? Action; internal readonly Action<DungeonPanelSnapshot>? Activate;
        internal Registration(DungeonPanelService owner, string plugin, string local, Func<DungeonPanelSnapshot, DungeonPanelSection?>? section, Func<DungeonPanelSnapshot, DungeonPanelAction?>? action, Action<DungeonPanelSnapshot>? activate, int order)
        { _owner = owner; Plugin = plugin; Local = local; Section = section; Action = action; Activate = activate; Order = order; }
        public void Dispose() { _owner._hub.CheckThread(); _owner._registrations.Remove(this); }
    }
}
