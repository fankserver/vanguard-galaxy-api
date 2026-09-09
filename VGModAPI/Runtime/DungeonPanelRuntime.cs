using System;
using System.Linq;
using UnityEngine;
using VGModAPI.Core;
namespace VGModAPI.Runtime;

internal sealed class DungeonPanelRuntime : IDungeonPanelSource, IDisposable
{
    private readonly BoardingObserver _observer;
    private readonly IBoardingService _boarding;
    private readonly BoardingCommandNativeBindings _native;
    private readonly Type _panelType;
    private readonly Func<object?, bool> _ready;
    private Component? _panel;
    private Guid _view;
    private long _revision;
    private DungeonPanelSnapshot? _snapshot;
    private bool _disposed;
    internal RectTransform? PanelRect => _panel ? _native.Call("panelRect", _panel) as RectTransform : null;
    internal TMPro.TMP_FontAsset? PanelFont => _panel ? (_native.Get(_panel, "panelLabel") as TMPro.TMP_Text)?.font : null;
    internal bool PresentationEnabled { get; set; }
    public DungeonPanelCapabilities Capabilities => new(!_disposed, !_disposed && PresentationEnabled, !_disposed && PresentationEnabled);
    internal DungeonPanelRuntime(GameBindings game, BoardingObserver observer, IBoardingService boarding, Func<object?, bool> ready)
    {
        _observer = observer; _boarding = boarding; _ready = ready;
        _panelType = game.Assembly.GetType(DungeonPanelBindings.Panel, true)!;
        _native = new(game, DungeonPanelBindings.Calls, DungeonPanelBindings.Members);
    }
    internal void Opened(object panel)
    { if (_disposed || panel is not Component component || !component) return; _panel = component; _view = Guid.NewGuid(); _revision = 0; _snapshot = null; }
    internal void Closed(object panel)
    { if (ReferenceEquals(_panel, panel)) { _panel = null; _snapshot = null; _view = Guid.Empty; } }
    public DungeonPanelSnapshot? Read()
    {
        if (_disposed || !_panel || !_panel!.gameObject.activeInHierarchy) { _snapshot = null; return null; }
        var location = _native.Get(_panel, "panelLocation"); var handle = _observer.CommandHandleForLocation(location);
        if (handle == null || !_observer.TryResolveCommandTarget(handle, out _, out _, out _)) { _snapshot = null; return null; }
        var target = _boarding.GetTarget(handle); if (target == null) { _snapshot = null; return null; }
        var operation = target.Operation == null ? null : _boarding.GetOperation(target.Operation);
        if (_snapshot == null || !_snapshot.Target.Handle.Equals(target.Handle) || _snapshot.Target.Revision != target.Revision || !Equals(_snapshot.Operation?.Handle, operation?.Handle) || _snapshot.Operation?.Revision != operation?.Revision)
            _snapshot = new(_view, ++_revision, target, operation);
        return _snapshot;
    }
    public DungeonPanelOpenStatus Open(BoardingHandle target)
    {
        if (_disposed) return DungeonPanelOpenStatus.Unavailable;
        if (!_observer.TryResolveCommandTarget(target, out var location, out var component, out var operation) || location == null || component is not Component live || !live) return DungeonPanelOpenStatus.StaleTarget;
        if (!_ready(operation)) return DungeonPanelOpenStatus.Busy;
        var panels = UnityEngine.Object.FindObjectsByType(_panelType, FindObjectsInactive.Include).OfType<Component>().Where(value => value).ToArray();
        if (panels.Length != 1) return DungeonPanelOpenStatus.Unavailable;
        var panel = panels[0]; var snapshot = _boarding.GetTarget(target); if (snapshot == null) return DungeonPanelOpenStatus.StaleTarget;
        _native.Call(snapshot.Kind == BoardingEncounterKind.Ship ? "panelOpenShip" : "panelOpenLocation", panel, snapshot.Kind == BoardingEncounterKind.Ship ? component : location);
        if (!panel || !panel.gameObject.activeInHierarchy) return DungeonPanelOpenStatus.Unavailable;
        if (!ReferenceEquals(_panel, panel)) Opened(panel);
        return Read()?.Target.Handle.Equals(target) == true ? DungeonPanelOpenStatus.Opened : DungeonPanelOpenStatus.StaleTarget;
    }
    public void Dispose() { _disposed = true; _panel = null; _snapshot = null; }
}
