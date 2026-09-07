using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using UnityEngine.EventSystems;
using VGModAPI.Core;

namespace VGModAPI.Menu;

internal sealed class ModMenuModule : IDisposable
{
    private readonly ModMenuBindings _bindings;
    private readonly ModMenuLifetime _lifetime;

    internal ModMenuModule(Assembly assembly, ModInformationCatalog catalog, Func<string> diagnostics, Action<Exception> fault, ModUpdatePresenter? updates = null)
    {
        _bindings = new ModMenuBindings(assembly);
        _lifetime = new ModMenuLifetime((menu, viewport, canvas) => ModMenuView.Create(
            (MonoBehaviour)menu, (RectTransform)viewport, (Canvas)canvas, _bindings,
            new ModInformationPresenter(catalog), diagnostics, fault, updates));
    }

    internal void Poll()
    {
        var menu = _bindings.ActiveMenu;
        var canvas = menu == null ? null : menu.GetComponentInParent<Canvas>();
        var viewport = menu == null || canvas == null ? null : ModMenuBindings.Viewport(menu, canvas);
        if (menu != null && (canvas == null || viewport == null || canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null))
            throw new InvalidOperationException("Inspected main-menu viewport/raycaster is unavailable.");
        var events = EventSystem.current;
        if (menu != null && (events == null || !(events.currentInputModule is InputSystemUIInputModule)))
            throw new InvalidOperationException("Inspected menu input module is unavailable.");
        _lifetime.Poll(menu == null ? null : menu, viewport == null ? null : viewport,
            canvas == null ? null : canvas, _bindings.ModalOpen);
    }

    public void Dispose() => _lifetime.Dispose();
}
