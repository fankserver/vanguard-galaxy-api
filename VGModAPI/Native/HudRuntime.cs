using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class HudRuntime : IDisposable
{
    private readonly HudService _service;
    private readonly Assembly _assembly;
    private readonly IReadOnlyDictionary<string, MethodInfo> _methods;
    private readonly HudPresentationSource _presentation;
    private readonly Func<Guid?> _session;
    private readonly Action<Exception> _report;
    private readonly HudIconResolver<Sprite> _icons;
    private Component? _indicator;
    private Canvas? _canvas;
    private GameObject? _root;
    private Guid _surface;
    private bool _disposed;
    private TMP_FontAsset? _font;
    internal HudRuntime(HudService service, Assembly assembly, IReadOnlyDictionary<string, MethodInfo> methods, Func<Guid?> session, Action<Exception> report)
    {
        _service = service; _assembly = assembly; _methods = methods; _presentation = new(assembly, methods);
        _session = session; _report = report; service.SurfaceLive = SurfaceLive;
        _icons = new(FindLauncherIcon, sprite => sprite != null, report);
    }
    private static Sprite? FindLauncherIcon(HudIcon icon)
    {
        Sprite? match = null;
        foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
        {
            if (sprite == null || !HudIconSprites.Matches(icon, sprite.name, sprite.rect.x, sprite.rect.y)) continue;
            // Duplicate names/atlas cells must not select a glyph by registry iteration order.
            if (match != null && match != sprite) return null;
            match = sprite;
        }
        return match;
    }
    internal void Tick()
    {
        if (_disposed) return;
        try { if (SyncSurface()) Render(); }
        catch (Exception error) { try { _report(error); } catch { } Dispose(); }
    }
    private bool SyncSurface()
    {
        var session = _session();
        var panel = _assembly.GetType("Behaviour.UI.Side_Menu.SidePanel", true)!.GetField("instance")!.GetValue(null) as Component;
        if (session == null || panel == null || !panel.gameObject.activeInHierarchy) { ClearSurface(); return false; }
        var indicators = panel.GetComponentsInChildren(_assembly.GetType("Behaviour.UI.Side_Menu.SideTabs.CargoIndicator", true)!, true)
            .Where(component => component is UnityEngine.Behaviour behaviour && behaviour.isActiveAndEnabled).ToArray();
        if (indicators.Length != 1) { ClearSurface(); return false; }
        var indicator = indicators[0]; var canvas = indicator.GetComponentInParent<Canvas>()?.rootCanvas;
        var font = panel.GetComponentsInChildren<TMP_Text>(true).Select(text => text.font).FirstOrDefault(value => value != null);
        if (canvas == null || !canvas.isActiveAndEnabled || canvas.renderMode == RenderMode.WorldSpace || font == null)
        { ClearSurface(); return false; }
        if (_indicator != indicator || _canvas != canvas || _service.Surface == null)
        {
            ClearSurface(); _indicator = indicator; _canvas = canvas; _font = font; _surface = Guid.NewGuid();
        }
        _service.SetSurface(_surface, session);
        return _service.Visible;
    }
    private bool SurfaceLive()
    {
        if (_disposed || _indicator == null || _canvas == null || !_canvas.isActiveAndEnabled || !_indicator.gameObject.activeInHierarchy || _indicator is not UnityEngine.Behaviour behaviour || !behaviour.isActiveAndEnabled || _session() == null) return false;
        var panel = _assembly.GetType("Behaviour.UI.Side_Menu.SidePanel", true)!.GetField("instance")!.GetValue(null) as Component;
        return panel != null && _indicator.transform.IsChildOf(panel.transform);
    }
    private void Click(Guid token, long revision, HudInteractionKind kind, string? row)
    {
        if (_disposed) return;
        var renderedSurface = _surface;
        try { if (SyncSurface() && _surface == renderedSurface) _service.Invoke(token, renderedSurface, revision, kind, row); }
        catch (Exception error) { try { _report(error); } catch { } Dispose(); }
    }
    private void ClearSurface()
    {
        ClearContent(); _icons.Clear(); _indicator = null; _canvas = null; _font = null; _service.SetSurface(null, null);
    }
    private void ClearContent()
    {
        if (_root != null) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        _root = null; _plainTooltip = null; _views.Clear(); _structure = "";
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; ClearSurface(); _service.SurfaceLive = null; _service.SetAvailable(false);
    }
}
