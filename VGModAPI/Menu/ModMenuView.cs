using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using VGModAPI.Core;
using Object = UnityEngine.Object;

namespace VGModAPI.Menu;

internal sealed class ModMenuView : IModMenuView
{
    private readonly MonoBehaviour _menu;
    private readonly RectTransform _viewport;
    private readonly Canvas _canvas;
    private readonly ModMenuBindings _bindings;
    private readonly ModInformationPresenter _presenter;
    private readonly Func<string> _diagnostics;
    private readonly Action<Exception> _fault;
    private Action<string> _openUrl = Application.OpenURL;
    private readonly List<Button> _rows = new();
    private readonly List<Selectable> _navigation = new();
    private readonly List<Action> _removeListeners = new();
    private readonly List<GameObject> _ownedRoots = new();
    private Button _entry = null!, _close = null!, _diagnosticToggle = null!, _previous = null!, _next = null!, _project = null!;
    private RectTransform _panel = null!, _body = null!, _listContent = null!, _detailsContent = null!;
    private ScrollRect _list = null!, _details = null!;
    private TMP_Text _heading = null!, _destination = null!, _detailText = null!;
    private TMP_FontAsset _font = null!;
    private Sprite? _sprite;
    private Image.Type _imageType;
    private float _pixelsPerUnitMultiplier;
    private bool _fillCenter, _preserveAspect;
    private ColorBlock _colors;
    private Color _buttonColor, _textColor;
    private GameObject? _savedFocus;
    private EventSystem? _events;
    private int _first = -1, _visibleCount = -1;
    private bool _rowsDirty = true;
    private float _width = -1, _height = -1;
    private bool _disposed;
    private bool _showDiagnostics;
    private readonly ModUpdatePresenter? _updates;
    private Button _checkUpdate = null!, _autoUpdate = null!, _release = null!;
    private string _updateText = "";

    private ModMenuView(MonoBehaviour menu, RectTransform viewport, Canvas canvas, ModMenuBindings bindings,
        ModInformationPresenter presenter, Func<string> diagnostics, Action<Exception> fault, ModUpdatePresenter? updates)
    { _menu = menu; _viewport = viewport; _canvas = canvas; _bindings = bindings; _presenter = presenter; _diagnostics = diagnostics; _fault = fault; _updates = updates; }

    internal static ModMenuView Create(MonoBehaviour menu, RectTransform viewport, Canvas canvas, ModMenuBindings bindings,
        ModInformationPresenter presenter, Func<string> diagnostics, Action<Exception> fault, ModUpdatePresenter? updates = null)
    {
        var view = new ModMenuView(menu, viewport, canvas, bindings, presenter, diagnostics, fault, updates);
        try { view.Build(); return view; }
        catch { view.Dispose(); throw; }
    }

    private bool Valid => !_disposed && _menu != null && _menu.isActiveAndEnabled && _viewport != null &&
        _canvas != null && _canvas.isActiveAndEnabled && _bindings.ActiveMenu == _menu &&
        _menu.GetComponentInParent<Canvas>() == _canvas && ModMenuBindings.Viewport(_menu, _canvas) == _viewport;
    private bool Open => _panel != null && _panel.gameObject.activeSelf;

    private void Build()
    {
        var native = _bindings.NativeStyle(_menu);
        var label = native.GetComponentInChildren<TMP_Text>(true);
        var image = native.GetComponent<Image>();
        _font = label != null ? label.font : _bindings.NativeVersion(_menu).font;
        if (_font == null || image == null || _menu.GetComponent<VerticalLayoutGroup>() == null)
            throw new InvalidOperationException("Native menu style/layout unavailable.");
        _sprite = image.sprite; _buttonColor = image.color; _colors = native.colors;
        _imageType = image.type; _pixelsPerUnitMultiplier = image.pixelsPerUnitMultiplier;
        _fillCenter = image.fillCenter; _preserveAspect = image.preserveAspect;
        _textColor = label != null ? label.color : Color.white;
        _entry = Button(_menu.transform, "VGModAPI Mods", "Mods", OpenPanel);
        _entry.GetComponentInChildren<TMP_Text>().alignment = TextAlignmentOptions.Center;
        // Let the native layout own its column. Only our new child participates in layout.
        _entry.navigation = new Navigation { mode = Navigation.Mode.Automatic };

        _panel = Rect(_viewport, "VGModAPI Mods panel");
        _panel.gameObject.SetActive(false);
        _panel.gameObject.AddComponent<LayoutElement>().ignoreLayout = true; // Gameview has a HorizontalLayoutGroup.
        Stretch(_panel, 0, 0, 1, 1);
        var backdrop = _panel.gameObject.AddComponent<Image>();
        backdrop.color = new Color(.035f, .045f, .06f, 1); backdrop.raycastTarget = true;
        _body = Rect(_panel, "Content");
        _heading = Text(_body, "Title", "Mods - local API consumers");
        Stretch(_heading.rectTransform, 0, 1, 1, 1, 8, -36, -112, -4);
        _close = Button(_body, "Close", "Close [Esc]", () => Close(true));
        _diagnosticToggle = Button(_body, "Diagnostics", "Show diagnostics", () =>
        {
            _showDiagnostics = !_showDiagnostics;
            _diagnosticToggle.GetComponentInChildren<TMP_Text>().text = _showDiagnostics ? "Hide diagnostics" : "Show diagnostics";
            RenderDetails();
        });
        Stretch((RectTransform)_close.transform, 1, 1, 1, 1, -108, -36, -4, -4);

        _list = Scroll(_body, "Local mods", out _listContent);
        Stretch((RectTransform)_list.transform, 0, 0, .38f, 1, 8, 46, -8, -42);
        _details = Scroll(_body, "Selected details", out _detailsContent);
        Stretch((RectTransform)_details.transform, .38f, 0, 1, 1, 4, 46, -8, -42);
        _destination = Text(_detailsContent, "ASCII project destination", "");
        _destination.alignment = TextAlignmentOptions.TopLeft;
        _destination.textWrappingMode = TextWrappingModes.Normal;
        _destination.overflowMode = TextOverflowModes.Overflow;
        _detailText = Text(_detailsContent, "Plain details", "");
        _detailText.alignment = TextAlignmentOptions.TopLeft;
        _detailText.textWrappingMode = TextWrappingModes.Normal;
        _detailText.overflowMode = TextOverflowModes.Overflow;
        Stretch(_detailText.rectTransform, 0, 0, 1, 1, 6, 0, -8, 0);
        _previous = Button(_body, "Previous mod", "Previous", () => MoveSelection(-1));
        _next = Button(_body, "Next mod", "Next", () => MoveSelection(1));
        Stretch((RectTransform)_previous.transform, 0, 0, .19f, 0, 8, 6, -4, 38);
        Stretch((RectTransform)_next.transform, .19f, 0, .38f, 0, 4, 6, -8, 38);
        _project = Button(_body, "Project link", "Open project in browser", () => _presenter.OpenProject(_openUrl));
        Stretch((RectTransform)_project.transform, .38f, 0, 1, 0, 4, 6, -8, 38);
        if (_updates != null)
        {
            _project.GetComponentInChildren<TMP_Text>().text = "Open project";
            Stretch((RectTransform)_project.transform, .38f, 0, .69f, 0, 4, 6, -4, 38);
            _release = Button(_body, "Release link", "Open release", () => { if (_presenter.Selected != null) _updates.OpenRelease(_presenter.Selected, _openUrl); });
            Stretch((RectTransform)_release.transform, .69f, 0, 1, 0, 4, 6, -8, 38);
            _checkUpdate = Button(_body, "Check update", "Check update", () => { if (_presenter.Selected != null) _updates.Check(_presenter.Selected); RenderDetails(true); });
            _autoUpdate = Button(_body, "Automatic updates", "Auto: off", () => { _updates.ToggleAutomatic(); RenderDetails(true); });
            Stretch((RectTransform)_checkUpdate.transform, .38f, 0, .69f, 0, 4, 46, -4, 78);
            Stretch((RectTransform)_autoUpdate.transform, .69f, 0, 1, 0, 4, 46, -8, 78);
            Stretch((RectTransform)_details.transform, .38f, 0, 1, 1, 4, 86, -8, -42);
        }
    }

    private void Guard(Action action)
    {
        try { if (Valid && !_bindings.ModalOpen) action(); }
        catch (Exception error) { _fault(error); }
    }

    private void OpenPanel()
    {
        if (Open) return;
        _events = EventSystem.current;
        _savedFocus = _events == null ? null : _events.currentSelectedGameObject;
        _presenter.Open();
        _updates?.Cancel();
        _updates?.Sync(_presenter.Rows);
        _panel.gameObject.SetActive(true); _panel.SetAsLastSibling();
        _entry.interactable = false;
        _listContent.anchoredPosition = Vector2.zero; _first = -1;
        _heading.text = "Mods - " + _presenter.Rows.Count + " local API consumers";
        Layout(); RenderDetails(); RefreshRows();
        Select(_close.gameObject);
    }

    public void Tick(bool modalOpen)
    {
        if (_disposed) return;
        if (_entry == null || _panel == null) throw new InvalidOperationException("Owned menu objects were removed.");
        if (!Valid || modalOpen) { Close(false); _entry.interactable = false; return; }
        _entry.interactable = !Open;
        if (!Open) return;
        if (_events != EventSystem.current) { Close(false); return; }
        Layout(); RefreshRows();
        if (_updates != null && _presenter.Selected != null && !_updates.Confirming &&
            _updates.Text(_presenter.Selected, DateTimeOffset.UtcNow) != _updateText) RenderDetails(true, false);
        if (Keyboard.current?.escapeKey.wasPressedThisFrame == true) { Close(true); return; }
        // All owned selectables use an explicit closed navigation ring; never edit native navigation.
        // Repair foreign/cleared selection without disabling the EventSystem or its input module.
        if (_events != null && !IsPanelFocus(_events.currentSelectedGameObject)) Select(_close.gameObject);
        if (Keyboard.current?.tabKey.wasPressedThisFrame == true)
        {
            var selected = _events == null ? null : _events.currentSelectedGameObject;
            var index = _navigation.FindIndex(item => item.gameObject == selected);
            var direction = Keyboard.current.shiftKey.isPressed ? -1 : 1;
            Select(_navigation[(index + direction + _navigation.Count) % _navigation.Count].gameObject);
        }
    }

    private void Layout()
    {
        var width = Mathf.Max(1, Mathf.Min(1120, _viewport.rect.width - 16));
        Stretch(_body, .5f, 0, .5f, 1, -width / 2, 0, width / 2, 0);
        _listContent.sizeDelta = new Vector2(0, _presenter.Rows.Count * ModMenuRows.Height);
        var caption = _close.GetComponentInChildren<TMP_Text>();
        var closeWidth = Mathf.Max(104, Mathf.Ceil(caption.GetPreferredValues(caption.text).x) + 24);
        Stretch((RectTransform)_close.transform, 1, 1, 1, 1, -closeWidth - 4, -36, -4, -4);
        var diagnosticCaption = _diagnosticToggle.GetComponentInChildren<TMP_Text>();
        var diagnosticWidth = Mathf.Ceil(diagnosticCaption.GetPreferredValues("Show diagnostics").x) + 24;
        Stretch((RectTransform)_diagnosticToggle.transform, 1, 1, 1, 1, -closeWidth - diagnosticWidth - 12, -36, -closeWidth - 12, -4);
        Stretch(_heading.rectTransform, 0, 1, 1, 1, 8, -36, -closeWidth - diagnosticWidth - 20, -4);
        var height = _viewport.rect.height;
        if (Math.Abs(_width - width) > .5f || Math.Abs(_height - height) > .5f)
        {
            _width = width; _height = height;
            Canvas.ForceUpdateCanvases();
            ResizeDetails();
        }
    }

    private void RefreshRows()
    {
        var count = ModMenuRows.VisibleCount(_presenter.Rows.Count, _list.viewport.rect.height);
        var first = ModMenuRows.First(_presenter.Rows.Count, _listContent.anchoredPosition.y, _list.viewport.rect.height);
        var changed = first != _first || _visibleCount != count;
        if (!changed && !_rowsDirty) return;
        _visibleCount = count; _rowsDirty = false;
        if (first != _first && _events != null && _rows.Exists(row => row != null && row.gameObject == _events.currentSelectedGameObject))
            Select(_close.gameObject); // A recycled row must not silently adopt keyboard activation.
        while (_rows.Count < count)
        {
            var slot = _rows.Count;
            var row = Button(_listContent, "Mod row " + slot, "", () =>
            {
                var index = _first + slot;
                if (index < _presenter.Rows.Count && _presenter.Select(_presenter.Rows[index].PluginId)) RenderDetails();
            });
            Stretch(row.GetComponentInChildren<TMP_Text>().rectTransform, 0, 0, 1, 1, 8, 0, -108, 0);
            var version = Text(row.transform, "Installed version", "");
            version.alignment = TextAlignmentOptions.MidlineRight;
            Stretch(version.rectTransform, 1, 0, 1, 1, -104, 0, -8, 0);
            _rows.Add(row);
        }
        _first = first;
        for (var slot = 0; slot < _rows.Count; ++slot)
        {
            var row = _rows[slot]; var index = first + slot;
            var visible = slot < count && index < _presenter.Rows.Count;
            row.gameObject.SetActive(visible);
            if (!visible) continue;
            var item = _presenter.Rows[index];
            Stretch((RectTransform)row.transform, 0, 1, 1, 1, 0, -(index + 1) * ModMenuRows.Height + 2, 0, -index * ModMenuRows.Height - 2);
            row.GetComponentInChildren<TMP_Text>().text = (item.PluginId == _presenter.SelectedId ? "> " : "") +
                ModInformationPresenter.DisplayName(item);
            row.transform.Find("Installed version").GetComponent<TMP_Text>().text = item.InstalledVersion.ToString();
        }
        if (changed) RebuildNavigation();
    }

    private void MoveSelection(int delta)
    {
        if (_presenter.Rows.Count == 0) return;
        var index = 0;
        for (var i = 0; i < _presenter.Rows.Count; ++i) if (_presenter.Rows[i].PluginId == _presenter.SelectedId) { index = i; break; }
        index = Mathf.Clamp(index + delta, 0, _presenter.Rows.Count - 1);
        _presenter.Select(_presenter.Rows[index].PluginId);
        _list.StopMovement();
        _listContent.anchoredPosition = new Vector2(0, Mathf.Min(index * ModMenuRows.Height,
            Mathf.Max(0, _listContent.rect.height - _list.viewport.rect.height)));
        RenderDetails(); RefreshRows();
    }

    private void RenderDetails(bool preserveConfirmation = false, bool resetScroll = true)
    {
        if (!preserveConfirmation) _updates?.Cancel();
        _rowsDirty = true;
        _detailText.text = _presenter.Details(_showDiagnostics ? _diagnostics() : "", _showDiagnostics, _updates == null);
        if (_updates != null)
        {
            ModUpdateControls.Apply(_presenter.Selected != null,
                _presenter.Selected != null && _updates.CanCheck(_presenter.Selected),
                _presenter.Selected != null && _updates.ReleaseHost(_presenter.Selected) != null,
                value => _checkUpdate.interactable = value, value => _autoUpdate.interactable = value,
                value => _release.interactable = value);
        }
        if (_updates != null && _presenter.Selected != null)
        {
            _updateText = _updates.Text(_presenter.Selected, DateTimeOffset.UtcNow);
            _detailText.text = _updates.Confirming ? _updateText : _updateText + "\n" + _detailText.text;
            _checkUpdate.GetComponentInChildren<TMP_Text>().text = _updates.Confirming && !_updates.ConfirmingAutomatic ? "Confirm check" : "Check update";
            _autoUpdate.GetComponentInChildren<TMP_Text>().text = _updates.Automatic ? "Auto: on" : _updates.ConfirmingAutomatic ? "Confirm auto" : "Auto: off";
        }
        _project.interactable = _presenter.TryProjectDestination(out var host);
        _destination.text = _project.interactable ? "Project destination (HTTPS):\n" + host : "No validated project link.";
        if (_updates != null && _presenter.Selected != null)
        {
            var releaseHost = _updates.ReleaseHost(_presenter.Selected);
            if (releaseHost != null) _destination.text += "\nRelease destination (HTTPS):\n" + releaseHost;
        }
        _previous.interactable = _next.interactable = _presenter.Rows.Count > 1;
        if (resetScroll) { _details.StopMovement(); _detailsContent.anchoredPosition = Vector2.zero; }
        ResizeDetails(); RebuildNavigation();
    }

    private void ResizeDetails()
    {
        var width = Mathf.Max(1, _details.viewport.rect.width - 14);
        var destinationHeight = _destination.GetPreferredValues(_destination.text, width, float.PositiveInfinity).y + 8;
        var detailHeight = _detailText.GetPreferredValues(_detailText.text, width, float.PositiveInfinity).y + 8;
        Stretch(_destination.rectTransform, 0, 1, 1, 1, 6, -destinationHeight, -8, 0);
        Stretch(_detailText.rectTransform, 0, 1, 1, 1, 6, -destinationHeight - detailHeight, -8, -destinationHeight);
        _detailsContent.sizeDelta = new Vector2(0, Mathf.Max(_details.viewport.rect.height, destinationHeight + detailHeight));
    }

    private void RebuildNavigation()
    {
        _navigation.Clear(); _navigation.Add(_close); _navigation.Add(_diagnosticToggle);
        foreach (var row in _rows) if (row.gameObject.activeSelf) _navigation.Add(row);
        if (_previous.interactable) _navigation.Add(_previous);
        if (_next.interactable) _navigation.Add(_next);
        if (_project.interactable) _navigation.Add(_project);
        if (_updates != null)
        {
            if (_checkUpdate.interactable) _navigation.Add(_checkUpdate);
            if (_autoUpdate.interactable) _navigation.Add(_autoUpdate);
            if (_release.interactable) _navigation.Add(_release);
        }
        _navigation.Add(_list.verticalScrollbar); _navigation.Add(_details.verticalScrollbar);
        for (var i = 0; i < _navigation.Count; ++i)
        {
            var links = ModMenuNavigation.Neighbors(i, _navigation.Count, _navigation[i] is Scrollbar);
            _navigation[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                selectOnUp = links.Up.HasValue ? _navigation[links.Up.Value] : null,
                selectOnDown = links.Down.HasValue ? _navigation[links.Down.Value] : null,
                selectOnLeft = _navigation[links.Left], selectOnRight = _navigation[links.Right] };
        }
    }

    private bool IsPanelFocus(GameObject? selected) => selected != null && selected.activeInHierarchy &&
        _panel != null && selected.transform.IsChildOf(_panel) &&
        selected.GetComponent<Selectable>() is Selectable selectable && selectable.IsInteractable();
    private void Select(GameObject target) { if (_events != null && _events == EventSystem.current) _events.SetSelectedGameObject(target); }

    private void Close(bool restore)
    {
        _updates?.Cancel();
        if (!Open) return;
        var events = _events;
        var ownedFocus = events != null && events == EventSystem.current && IsPanelFocus(events.currentSelectedGameObject);
        _panel.gameObject.SetActive(false);
        if (_entry != null) _entry.interactable = true;
        if (ownedFocus) events!.SetSelectedGameObject(null);
        if (restore && Valid && !_bindings.ModalOpen && events != null && events == EventSystem.current &&
            _savedFocus != null && _savedFocus.activeInHierarchy && _savedFocus.transform.IsChildOf(_menu.transform) &&
            _savedFocus.GetComponent<Selectable>() is Selectable selectable && selectable.IsInteractable())
            events.SetSelectedGameObject(_savedFocus);
        _savedFocus = null; _events = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Exception? failure = null;
        void Cleanup(Action action) { try { action(); } catch (Exception error) { failure ??= error; } }
        Cleanup(() => Close(false)); _disposed = true;
        _savedFocus = null; _events = null;
        foreach (var remove in _removeListeners) Cleanup(remove);
        _removeListeners.Clear();
        foreach (var root in _ownedRoots)
            if (root != null)
            {
                Cleanup(() => root.SetActive(false));
                Cleanup(() => Object.Destroy(root));
            }
        _ownedRoots.Clear(); _rows.Clear(); _navigation.Clear();
        // Shared native font/sprite/material objects are never modified or destroyed.
        if (failure != null) throw new InvalidOperationException("Owned menu cleanup failed.", failure);
    }

    private Button Button(Transform parent, string name, string caption, Action click)
    {
        var rect = Rect(parent, name);
        var image = rect.gameObject.AddComponent<Image>(); image.sprite = _sprite;
        image.type = _imageType; image.pixelsPerUnitMultiplier = _pixelsPerUnitMultiplier;
        image.fillCenter = _fillCenter; image.preserveAspect = _preserveAspect; image.color = _buttonColor;
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image; button.colors = _colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        var label = Text(rect, "Label", caption); Stretch(label.rectTransform, 0, 0, 1, 1, 8, 0, -8, 0);
        UnityAction action = () => Guard(click); button.onClick.AddListener(action);
        _removeListeners.Add(() => { if (button != null) button.onClick.RemoveListener(action); });
        return button;
    }

    private TMP_Text Text(Transform parent, string name, string value)
    {
        var label = Rect(parent, name).gameObject.AddComponent<TextMeshProUGUI>();
        label.font = _font; label.fontSize = 16; label.color = _textColor;
        label.richText = false; label.parseCtrlCharacters = false; label.raycastTarget = false;
        label.textWrappingMode = TextWrappingModes.NoWrap; label.overflowMode = TextOverflowModes.Truncate;
        label.alignment = TextAlignmentOptions.MidlineLeft; label.text = value;
        return label;
    }

    private ScrollRect Scroll(Transform parent, string name, out RectTransform content)
    {
        var root = Rect(parent, name);
        var scroll = root.gameObject.AddComponent<ScrollRect>();
        var viewport = Rect(root, "Viewport"); Stretch(viewport, 0, 0, 1, 1, 0, 0, -18, 0);
        viewport.gameObject.AddComponent<RectMask2D>();
        var background = viewport.gameObject.AddComponent<Image>(); background.color = new Color(.08f, .1f, .13f, 1);
        content = Rect(viewport, "Content"); Stretch(content, 0, 1, 1, 1); content.pivot = new Vector2(.5f, 1);
        var barRect = Rect(root, "Scrollbar"); Stretch(barRect, 1, 0, 1, 1, -16, 0, 0, 0);
        var track = barRect.gameObject.AddComponent<Image>(); track.color = new Color(.12f, .15f, .19f, 1);
        var handle = Rect(barRect, "Handle");
        Stretch(handle, 0, 0, 1, 1); // Scrollbar drives anchors, not offsets.
        var handleImage = handle.gameObject.AddComponent<Image>();
        handleImage.color = new Color(.55f, .6f, .68f, 1);
        var bar = barRect.gameObject.AddComponent<Scrollbar>(); bar.handleRect = handle; bar.targetGraphic = handleImage;
        bar.direction = Scrollbar.Direction.BottomToTop;
        scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 36; scroll.inertia = false;
        scroll.verticalScrollbar = bar; scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        return scroll;
    }

    private RectTransform Rect(Transform parent, string name)
    {
        var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        if (parent == _menu.transform || parent == _viewport) _ownedRoots.Add(rect.gameObject);
        rect.SetParent(parent, false); return rect;
    }

    private static void Stretch(RectTransform rect, float minX, float minY, float maxX, float maxY,
        float left = 0, float bottom = 0, float right = 0, float top = 0)
    {
        rect.anchorMin = new Vector2(minX, minY); rect.anchorMax = new Vector2(maxX, maxY);
        rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(right, top);
    }
}
