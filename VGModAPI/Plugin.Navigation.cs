using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using VGModAPI.Core;
using VGModAPI.Core.Integration;

namespace VGModAPI;

public sealed partial class Plugin
{
    private NavigationService? _navigationService;
    private NavigationFocusRoutine? _navigationFocus;
    private NavigationNativeReader? _navigationReader;
    private PropertyInfo? _navigationMap;
    private FieldInfo? _navigationPanel;
    private PropertyInfo? _navigationTarget;
    private MethodInfo? _navigationOpen;
    private object? NavigationMap(Guid session)
    {
        if (_adapter == null || !_adapter.TryGetObservedPlayer(session, out var player)) return null;
        return _navigationMap?.GetValue(player);
    }
    private NavigationService CreateNavigation() => new(_hub!, session =>
    {
        var map = NavigationMap(session);
        return map == null ? null : _navigationReader?.Read(map, () => ReferenceEquals(NavigationMap(session), map));
    }, FocusNavigation, (owner, id) => _worldReferences?.Knows(owner, id));
    private void InitializeNavigation()
    {
        _navigationService ??= CreateNavigation();
        _hub!.SetCapability("navigation", false, "Navigation bindings are initializing.");
        try
        {
            var assembly = Assembly.Load("Assembly-CSharp");
            _navigationReader = new NavigationNativeReader(assembly);
            _navigationMap = assembly.GetType("Source.Player.GamePlayer", true)!.GetProperty("map")!;
            var panel = assembly.GetType("Behaviour.UI.Side_Menu.SidePanel", true)!;
            _navigationPanel = panel.GetField("instance", BindingFlags.Public | BindingFlags.Static)!;
            _navigationTarget = panel.GetProperty("focussedPoi", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            _navigationOpen = panel.GetMethod("OpenMapAndFocusPoi", BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (_navigationMap == null || _navigationPanel == null || _navigationTarget == null || _navigationOpen == null)
                throw new MissingMemberException("Navigation UI bindings unavailable.");
            _hub.Changed += NavigationLifecycle;
            _hub.SetCapability("navigation", true, "Read-only station metadata, unweighted jump counts and map focus requests.");
        }
        catch (Exception error) { _hub.SetCapability("navigation", false, error.Message); }
    }
    private NavigationStatus FocusNavigation(Guid session, string id, Func<bool> admitted)
    {
        var map = NavigationMap(session); var panel = _navigationPanel?.GetValue(null) as MonoBehaviour;
        if (map == null || panel == null || _navigationReader == null) return NavigationStatus.NotReady;
        if (_navigationFocus != null || _navigationTarget!.GetValue(panel) != null) return NavigationStatus.Rejected;
        var poi = _navigationReader.FindPoint(map, id);
        if (poi == null || _navigationReader.Hidden(poi)) return NavigationStatus.Missing;
        var native = _navigationOpen!.Invoke(panel, new[] { poi, (object)false }) as IEnumerator;
        if (native == null) return NavigationStatus.Unavailable;
        NavigationFocusRoutine? routine = null;
        routine = new NavigationFocusRoutine(native, poi,
            () => panel != null && admitted() && ReferenceEquals(NavigationMap(session), map) && ReferenceEquals(_navigationReader.FindPoint(map, id), poi),
            () => panel == null ? null : _navigationTarget.GetValue(panel),
            () => _navigationTarget.SetValue(panel, null), () => { if (ReferenceEquals(_navigationFocus, routine)) _navigationFocus = null; });
        _navigationFocus = routine;
        try { StartCoroutine(routine); }
        catch { routine.Dispose(); throw; }
        return NavigationStatus.Succeeded;
    }
    private void NavigationLifecycle(LifecycleEvent change)
    {
        if (change.Kind == LifecycleEventKind.SessionStarting || change.Kind == LifecycleEventKind.SessionInvalidated || change.Kind == LifecycleEventKind.SessionStartFailed)
            _navigationFocus?.Dispose();
    }
    private void StopNavigation()
    {
        if (_hub != null) _hub.Changed -= NavigationLifecycle;
        _navigationFocus?.Dispose(); _navigationReader = null;
    }
}
