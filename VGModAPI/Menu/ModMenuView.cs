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
    private readonly Action<Exception> _fault;
    private Action<string> _openUrl = Application.OpenURL;
    private readonly List<Button> _rows = new();
    private readonly List<Selectable> _navigation = new();
    private readonly List<Action> _removeListeners = new();
    private readonly List<GameObject> _ownedRoots = new();
    private Button _entry = null!, _close = null!, _project = null!;
    private RectTransform _panel = null!, _body = null!, _listContent = null!, _detailsContent = null!;
    private ScrollRect _list = null!, _details = null!;
    private TMP_Text _heading = null!, _summary = null!, _updateStatus = null!, _detailText = null!;
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
    private readonly ModUpdatePresenter? _updates;
    private Button _checkUpdate = null!, _release = null!;
    private string _updateText = "";
    private string _listStatus = "";
    private float _nextStatusRefresh;

    private ModMenuView(MonoBehaviour menu, RectTransform viewport, Canvas canvas, ModMenuBindings bindings,
        ModInformationPresenter presenter, Action<Exception> fault, ModUpdatePresenter? updates)
    { _menu = menu; _viewport = viewport; _canvas = canvas; _bindings = bindings; _presenter = presenter; _fault = fault; _updates = updates; }

    internal static ModMenuView Create(MonoBehaviour menu, RectTransform viewport, Canvas canvas, ModMenuBindings bindings,
        ModInformationPresenter presenter, Action<Exception> fault, ModUpdatePresenter? updates = null)
    {
        var view = new ModMenuView(menu, viewport, canvas, bindings, presenter, fault, updates);
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
        _heading = Text(_body, "Title", "Mods");
        Stretch(_heading.rectTransform, 0, 1, 1, 1, 8, -36, -112, -4);
        _close = Button(_body, "Close", "Close [Esc]", () => Close(true));
        Stretch((RectTransform)_close.transform, 1, 1, 1, 1, -108, -36, -4, -4);

        _summary = Text(_body, "Update summary", "");
        _summary.fontSize = 14;
        Stretch(_summary.rectTransform, 0, 1, 1, 1, 8, -64, -8, -38);
        _list = Scroll(_body, "Local mods", out _listContent);
        Stretch((RectTransform)_list.transform, 0, 0, .46f, 1, 8, 52, -8, -70);
        _details = Scroll(_body, "Selected details", out _detailsContent);
        Stretch((RectTransform)_details.transform, .46f, 0, 1, 1, 8, 122, -8, -70);
        _detailText = Text(_detailsContent, "Plain details", "");
        _detailText.alignment = TextAlignmentOptions.TopLeft;
        _detailText.textWrappingMode = TextWrappingModes.Normal;
        _detailText.overflowMode = TextOverflowModes.Overflow;
        Stretch(_detailText.rectTransform, 0, 0, 1, 1, 6, 0, -8, 0);
        _project = Button(_body, "Project link", "Mod website", () => _presenter.OpenProject(_openUrl));
        Stretch((RectTransform)_project.transform, .46f, 0, .70f, 0, 8, 8, -4, 40);
        var divider = Rect(_body, "Update divider");
        Stretch(divider, .46f, 0, 1, 0, 8, 74, -8, 75);
        divider.gameObject.AddComponent<Image>().color = new Color(.2f, .3f, .36f, 1);
        _updateStatus = Text(_body, "Update status", "Update checking unavailable");
        _updateStatus.fontSize = 14;
        _updateStatus.alignment = TextAlignmentOptions.TopLeft;
        _updateStatus.textWrappingMode = TextWrappingModes.Normal;
        Stretch(_updateStatus.rectTransform, .46f, 0, 1, 0, 8, 8, -170, 68);
        if (_updates != null)
        {
            _release = Button(_body, "Release link", "Download update", () => { if (_presenter.Selected != null) _updates.OpenRelease(_presenter.Selected, _openUrl); });
            Stretch((RectTransform)_release.transform, 1, 0, 1, 0, -168, 24, -8, 56);
            _checkUpdate = Button(_body, "Check updates", "Check for updates", () => { _updates.CheckAll(_presenter.Rows); RenderDetails(false); });
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
        _updates?.Sync(_presenter.Rows);
        _panel.gameObject.SetActive(true); _panel.SetAsLastSibling();
        _entry.interactable = false;
        _listContent.anchoredPosition = Vector2.zero; _first = -1;
        _heading.text = "Mods";
        _summary.text = _updates?.Summary(_presenter.Rows) ?? (_presenter.Rows.Count + " mods");
        _nextStatusRefresh = 0;
        Layout(); RenderDetails(); RefreshRows();
        Select(_close.gameObject);
    }

    public void Tick(bool modalOpen)
    {
        if (_disposed) return;
        if (_entry == null || _panel == null) throw new InvalidOperationException("Owned menu objects were removed.");
        if (!Valid || modalOpen) { Close(false); _entry.interactable = false; return; }
        _entry.interactable = !Open;
        var refreshStatus = Time.unscaledTime >= _nextStatusRefresh;
        if (refreshStatus)
        {
            _nextStatusRefresh = Time.unscaledTime + .25f;
            var available = _updates?.AvailableCount(ModApi.Mods?.Snapshot ?? Array.Empty<ModInformation>()) ?? 0;
            _entry.GetComponentInChildren<TMP_Text>().text = available > 0 ? "Mods (" + available + (available == 1 ? " update)" : " updates)") : "Mods";
        }
        if (!Open) return;
        if (_events != EventSystem.current) { Close(false); return; }
        Layout();
        if (refreshStatus)
        {
            var listStatus = string.Join("|", System.Linq.Enumerable.Select(_presenter.Rows, mod => _updates?.Label(mod) ?? "Check unavailable"));
            if (listStatus != _listStatus) { _listStatus = listStatus; _rowsDirty = true; }
            _summary.text = _updates?.Summary(_presenter.Rows) ?? (_presenter.Rows.Count + " mods");
        }
        RefreshRows();
        UpdateScrollbars();
        if (_updates != null && _presenter.Selected != null &&
            _updates.Text(_presenter.Selected, DateTimeOffset.UtcNow) != _updateText) RenderDetails(false);
        var directionKey = Keyboard.current?.downArrowKey.wasPressedThisFrame == true ? 1 :
            Keyboard.current?.upArrowKey.wasPressedThisFrame == true ? -1 : 0;
        if (_events != null && directionKey != 0)
        {
            var slot = _rows.FindIndex(row => row.gameObject == _events.currentSelectedGameObject);
            if (slot >= 0 && _first + slot < _presenter.Rows.Count)
            {
                _presenter.Select(_presenter.Rows[_first + slot].PluginId);
                MoveSelection(directionKey);
            }
        }
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
        Stretch(_heading.rectTransform, 0, 1, 1, 1, 8, -36, -closeWidth - 20, -4);
        if (_updates != null)
        {
            var checkWidth = Mathf.Ceil(_checkUpdate.GetComponentInChildren<TMP_Text>().GetPreferredValues("Check for updates").x) + 24;
            Stretch((RectTransform)_checkUpdate.transform, 0, 0, 0, 0, 8, 8, 8 + checkWidth, 40);
        }
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
            var name = row.GetComponentInChildren<TMP_Text>();
            name.enableAutoSizing = false;
            name.textWrappingMode = TextWrappingModes.Normal;
            name.overflowMode = TextOverflowModes.Ellipsis;
            name.alignment = TextAlignmentOptions.TopLeft;
            Stretch(name.rectTransform, 0, 0, 1, 1, 10, 28, -10, -8);
            var version = Text(row.transform, "Installed version", "");
            version.fontSize = 14;
            version.color = new Color(.7f, .75f, .8f, 1);
            Stretch(version.rectTransform, 0, 0, .35f, 0, 10, 4, 0, 26);
            var status = Text(row.transform, "Update state", "");
            status.fontSize = 14;
            status.alignment = TextAlignmentOptions.MidlineRight;
            Stretch(status.rectTransform, .35f, 0, 1, 0, 0, 4, -10, 26);
            var stripe = Rect(row.transform, "Selection stripe");
            Stretch(stripe, 0, 0, 0, 1, 0, 0, 3, 0);
            var stripeImage = stripe.gameObject.AddComponent<Image>();
            stripeImage.color = new Color(.4f, .85f, 1, 1); stripeImage.raycastTarget = false;
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
            row.GetComponentInChildren<TMP_Text>().text = ModInformationPresenter.DisplayName(item);
            // Selection persists independently of keyboard focus and pointer hover.
            var colors = _colors;
            if (item.PluginId == _presenter.SelectedId)
            {
                colors.normalColor = new Color(.25f, .65f, .85f, 1);
                colors.highlightedColor = new Color(.4f, .8f, 1, 1);
                colors.selectedColor = colors.highlightedColor;
            }
            row.colors = colors;
            row.transform.Find("Selection stripe").gameObject.SetActive(item.PluginId == _presenter.SelectedId);
            row.transform.Find("Installed version").GetComponent<TMP_Text>().text = "v" + item.InstalledVersion;
            var status = row.transform.Find("Update state").GetComponent<TMP_Text>();
            status.text = _updates?.Label(item) ?? "Check unavailable";
            status.color = StatusColor(item);
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
        var slot = index - _first;
        if (slot >= 0 && slot < _rows.Count) Select(_rows[slot].gameObject);
    }

    private Color StatusColor(ModInformation mod) => (_updates?.State(mod)) switch
    {
        ModUpdateState.Available => new Color(.4f, .85f, 1, 1),
        ModUpdateState.Current => new Color(.55f, .85f, .65f, 1),
        ModUpdateState.Failed or ModUpdateState.Invalid or ModUpdateState.RateLimited => new Color(1, .72f, .4f, 1),
        _ => new Color(.7f, .75f, .8f, 1)
    };

    private void RenderDetails(bool resetScroll = true)
    {
        _rowsDirty = true;
        _detailText.text = _presenter.Details();
        if (_updates != null)
        {
            _checkUpdate.interactable = System.Linq.Enumerable.Any(_presenter.Rows, mod => _updates.CanCheck(mod));
            _release.gameObject.SetActive(_presenter.Selected != null && _updates.ReleaseHost(_presenter.Selected) != null);
        }
        if (_updates != null && _presenter.Selected != null)
        {
            _updateText = _updates.Text(_presenter.Selected, DateTimeOffset.UtcNow);
            _updateStatus.text = _updateText;
            _updateStatus.color = StatusColor(_presenter.Selected);
        }
        _project.gameObject.SetActive(_presenter.TryProjectDestination(out _));
        if (_presenter.Selected == null) _updateStatus.text = "";
        if (resetScroll) { _details.StopMovement(); _detailsContent.anchoredPosition = Vector2.zero; }
        ResizeDetails(); UpdateScrollbars(); RebuildNavigation();
    }

    private void ResizeDetails()
    {
        _updateStatus.fontSize = _body.rect.width < 800 ? 12 : 14;
        var updateWidth = Mathf.Max(1, _body.rect.width * .54f - 24);
        var updateHeight = Mathf.Max(44, _updateStatus.GetPreferredValues(_updateStatus.text, updateWidth, float.PositiveInfinity).y + 8);
        Stretch(_updateStatus.rectTransform, .46f, 0, 1, 0, 16, 52, -8, updateHeight + 52);
        Stretch((RectTransform)_body.Find("Update divider"), .46f, 0, 1, 0, 8, updateHeight + 60, -8, updateHeight + 61);
        Stretch((RectTransform)_project.transform, .46f, 0, .70f, 0, 8, 8, -4, 40);
        Stretch((RectTransform)_details.transform, .46f, 0, 1, 1, 8, updateHeight + 69, -8, -70);
        if (_updates != null)
            Stretch((RectTransform)_release.transform, .70f, 0, 1, 0, 4, 8, -8, 40);
        Canvas.ForceUpdateCanvases();
        var width = Mathf.Max(1, _details.viewport.rect.width - 14);
        var detailHeight = _detailText.GetPreferredValues(_detailText.text, width, float.PositiveInfinity).y + 16;
        Stretch(_detailText.rectTransform, 0, 1, 1, 1, 8, -detailHeight, -8, -8);
        _detailsContent.sizeDelta = new Vector2(0, Mathf.Max(_details.viewport.rect.height, detailHeight));
    }

    private void UpdateScrollbars()
    {
        var changed = false;
        foreach (var scroll in new[] { _list, _details })
        {
            var visible = scroll.content.rect.height > scroll.viewport.rect.height + 1;
            if (scroll.verticalScrollbar.gameObject.activeSelf == visible) continue;
            scroll.verticalScrollbar.gameObject.SetActive(visible);
            changed = true;
        }
        if (changed) RebuildNavigation();
    }

    private void RebuildNavigation()
    {
        _navigation.Clear(); _navigation.Add(_close);
        foreach (var row in _rows) if (row.gameObject.activeSelf) _navigation.Add(row);
        if (_project.gameObject.activeSelf && _project.interactable) _navigation.Add(_project);
        if (_updates != null)
        {
            if (_checkUpdate.interactable) _navigation.Add(_checkUpdate);
            if (_release.gameObject.activeSelf && _release.interactable) _navigation.Add(_release);
        }
        if (_list.verticalScrollbar.gameObject.activeSelf) _navigation.Add(_list.verticalScrollbar);
        if (_details.verticalScrollbar.gameObject.activeSelf) _navigation.Add(_details.verticalScrollbar);
        for (var i = 0; i < _navigation.Count; ++i)
        {
            var links = ModMenuNavigation.Neighbors(i, _navigation.Count, _navigation[i] is Scrollbar);
            _navigation[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                selectOnUp = !_rows.Exists(row => row == _navigation[i]) && links.Up.HasValue ? _navigation[links.Up.Value] : null,
                selectOnDown = !_rows.Exists(row => row == _navigation[i]) && links.Down.HasValue ? _navigation[links.Down.Value] : null,
                selectOnLeft = _navigation[links.Left], selectOnRight = _navigation[links.Right] };
        }
    }

    private bool IsPanelFocus(GameObject? selected) => selected != null && selected.activeInHierarchy &&
        _panel != null && selected.transform.IsChildOf(_panel) &&
        selected.GetComponent<Selectable>() is Selectable selectable && selectable.IsInteractable();
    private void Select(GameObject target) { if (_events != null && _events == EventSystem.current) _events.SetSelectedGameObject(target); }

    private void Close(bool restore)
    {
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
        label.enableAutoSizing = true; label.fontSizeMin = 12; label.fontSizeMax = 16;
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
        label.margin = Vector4.zero;
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
        scroll.verticalScrollbar = bar; scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
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
