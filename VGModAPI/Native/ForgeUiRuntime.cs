using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class ForgeUiRuntime : IDisposable
{
    private readonly ForgeUiService _service;
    private readonly RecipeCatalogNativeSource _source;
    private readonly Action<Exception> _report;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private Exception? _fault;
    private bool _disposed;
    private ForgeUiWatcher? _watcher;
    private GameObject? _root, _tooltip;
    private RectTransform? _content;
    private TMP_Text? _tooltipText;
    private ForgeViewHandle? _view;
    private readonly List<Row> _rows = new();
    private readonly Vector3[] _tabCorners = new Vector3[4];
    internal ForgeUiRuntime(ForgeUiService service, RecipeCatalogNativeSource source, Action<Exception> report)
    { _service = service; _source = source; _report = report; }
    internal void Observe(Exception? nativeError = null)
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId != _thread)
        { Interlocked.CompareExchange(ref _fault, new InvalidOperationException("Forge UI hook invoked off the main thread."), null); return; }
        try
        {
            if (nativeError != null) throw new InvalidOperationException("Native Forge UI operation failed.", nativeError);
            _service.Refresh();
            if (_source.ForgeViewObject is MonoBehaviour component && component != null)
            {
                if (_watcher == null || _watcher.gameObject != component.gameObject)
                {
                    if (_watcher != null) _watcher.Owner = null;
                    _watcher = component.GetComponent<ForgeUiWatcher>() ?? component.gameObject.AddComponent<ForgeUiWatcher>();
                    _watcher.Owner = this;
                }
            }
            else { if (_watcher != null) _watcher.Owner = null; _watcher = null; ClearView(); }
        }
        catch (Exception error) { Interlocked.CompareExchange(ref _fault, error, null); }
    }
    internal void Tick()
    {
        if (_disposed) return;
        if (Volatile.Read(ref _fault) is Exception error)
        { try { _report(error); } catch { } Dispose(); return; }
        Observe();
        if (Volatile.Read(ref _fault) != null) return;
        try { Render(); } catch (Exception failure) { Interlocked.CompareExchange(ref _fault, failure, null); }
    }
    private void Render()
    {
        var snapshot = _service.Current; var actions = _service.Actions;
        if (snapshot == null || actions.Count == 0) { ClearView(); return; }
        var ui = _source.ForgeViewObject!;
        var contents = RecipeCatalogNativeSource.Member(ui, "tabContents")!;
        var icon = RecipeCatalogNativeSource.Member(contents, "recipeIcon") as Image;
        var font = (RecipeCatalogNativeSource.Member(contents, "costText") as TMP_Text)?.font;
        var tabs = _source.ForgeTabAnchor as RectTransform;
        var canvas = tabs != null ? tabs.GetComponentInParent<Canvas>()?.rootCanvas : null;
        if (icon == null || tabs == null || canvas == null || canvas.transform is not RectTransform anchor || font == null)
            throw new InvalidOperationException("Forge action anchor unavailable.");
        // A temporarily small canvas hides presentation without destroying registrations or the capability.
        if (!TryGetBand(tabs, anchor, actions.Count, out var band)) { ClearView(); return; }
        if (_root == null || _root.transform.parent != anchor || _view?.Equals(snapshot.View) != true || !_rows.Select(row => row.Token).SequenceEqual(actions.Select(action => action.Token)))
        {
            ClearView(); _view = snapshot.View;
            _root = new GameObject("Mod API Forge actions", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            var root = (RectTransform)_root.transform; root.SetParent(anchor, false);
            // Canvas-owned, outside recipe content and tab masks; ClearView owns removal on native view teardown.
            root.anchorMin = root.anchorMax = root.pivot = Vector2.zero;
            var stationBranch = tabs.transform;
            while (stationBranch.parent != anchor && stationBranch.parent != null) stationBranch = stationBranch.parent;
            root.SetSiblingIndex(stationBranch.GetSiblingIndex() + 1);
            _root.GetComponent<Image>().color = new Color(.03f, .04f, .06f, .9f);
            _content = new GameObject("Actions", typeof(RectTransform)).GetComponent<RectTransform>(); _content.SetParent(root, false);
            _content.anchorMin = new Vector2(0, 0); _content.anchorMax = new Vector2(0, 1); _content.pivot = new Vector2(0, .5f);
            _content.sizeDelta = new Vector2(actions.Count * (ForgeActionBand.CellWidth + 4) - 4, 0);
            var scroll = _root.GetComponent<ScrollRect>(); scroll.viewport = root; scroll.content = _content;
            scroll.horizontal = true; scroll.vertical = false; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 24;
            _tooltip = new GameObject("Forge action tooltip", typeof(RectTransform), typeof(Image));
            var tipRect = (RectTransform)_tooltip.transform; tipRect.SetParent(anchor, false);
            tipRect.anchorMin = tipRect.anchorMax = tipRect.pivot = Vector2.zero;
            tipRect.SetSiblingIndex(root.GetSiblingIndex() + 1);
            var tipImage = _tooltip.GetComponent<Image>(); tipImage.color = new Color(.02f, .03f, .05f, .98f); tipImage.raycastTarget = false;
            _tooltipText = Text(tipRect, font); _tooltipText.fontSize = 12; _tooltipText.alignment = TextAlignmentOptions.MidlineLeft;
            _tooltip.SetActive(false);
            for (var index = 0; index < actions.Count; index++)
            {
                var action = actions[index];
                var go = new GameObject("Action", typeof(RectTransform), typeof(Image), typeof(RevisionButton), typeof(ForgeActionHover));
                var rect = (RectTransform)go.transform; rect.SetParent(_content, false); rect.anchorMin = new Vector2(0, 0); rect.anchorMax = new Vector2(0, 1);
                rect.pivot = new Vector2(0, .5f); rect.sizeDelta = new Vector2(ForgeActionBand.CellWidth, 0); rect.anchoredPosition = new Vector2(index * (ForgeActionBand.CellWidth + 4), 0);
                var image = go.GetComponent<Image>(); image.color = new Color(.1f, .17f, .22f, 1);
                var button = go.GetComponent<RevisionButton>(); button.targetGraphic = image;
                var row = new Row(action.Token, button, Text(rect, font), go.GetComponent<ForgeActionHover>());
                var sprite = new GameObject("Selection icon", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                var spriteRect = (RectTransform)sprite.transform; spriteRect.SetParent(rect, false); spriteRect.anchorMin = spriteRect.anchorMax = new Vector2(0, .5f);
                spriteRect.pivot = new Vector2(0, .5f); spriteRect.sizeDelta = new Vector2(20, 20); spriteRect.anchoredPosition = new Vector2(2, 0);
                sprite.preserveAspect = true; sprite.raycastTarget = false; row.Icon = sprite;
                button.ReadRevision = () => row.Revision;
                button.onClick.AddListener(() => { if (row.View != null) _service.Invoke(row.Token, row.View, button.InvocationRevision); });
                row.Hover.Show = text => { if (_tooltip != null && _tooltipText != null) { _tooltipText.text = text; _tooltip.SetActive(!string.IsNullOrEmpty(text)); } };
                _rows.Add(row);
            }
        }
        PlaceBand(band, (RectTransform)_root.transform, (RectTransform)_tooltip!.transform);
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index]; var presentation = actions[index].Presentation;
            row.View = snapshot.View; row.Revision = snapshot.Revision; row.Button.interactable = presentation.Enabled;
            row.Label.text = presentation.Label; row.Hover.Tooltip = presentation.Tooltip;
            var showIcon = presentation.UseSelectionIcon && icon.sprite != null;
            row.Icon!.sprite = showIcon ? icon.sprite : null; row.Icon.gameObject.SetActive(showIcon);
            row.Label.rectTransform.offsetMin = new Vector2(showIcon ? 24 : 4, 0);
        }
    }
    private bool TryGetBand(RectTransform tabs, RectTransform canvas, int count, out ForgeActionBand band)
    {
        tabs.GetWorldCorners(_tabCorners);
        var left = canvas.InverseTransformPoint(_tabCorners[1]); var right = canvas.InverseTransformPoint(_tabCorners[2]);
        var bounds = canvas.rect;
        return ForgeActionBand.TryCreate(bounds.width, bounds.height, left.x - bounds.xMin, right.x - bounds.xMin,
            Math.Max(left.y, right.y) - bounds.yMin, out band, count * (ForgeActionBand.CellWidth + 4) - 4);
    }
    private static void PlaceBand(ForgeActionBand band, RectTransform strip, RectTransform tooltip)
    {
        strip.anchoredPosition = new Vector2(band.Left, band.Bottom); strip.sizeDelta = new Vector2(band.Width, ForgeActionBand.Height);
        tooltip.anchoredPosition = new Vector2(band.Left, band.TooltipBottom); tooltip.sizeDelta = new Vector2(band.Width, ForgeActionBand.TooltipHeight);
    }
    private static TMP_Text Text(RectTransform parent, TMP_FontAsset font)
    {
        var text = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
        text.transform.SetParent(parent, false); text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = new Vector2(4, 0); text.rectTransform.offsetMax = new Vector2(-4, 0);
        text.font = font; text.fontSize = 12; text.alignment = TextAlignmentOptions.Center;
        text.richText = false; text.raycastTarget = false; text.overflowMode = TextOverflowModes.Truncate; return text;
    }
    private void ClearView()
    {
        if (_root != null) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        if (_tooltip != null) { _tooltip.SetActive(false); UnityEngine.Object.Destroy(_tooltip); }
        _root = null; _tooltip = null; _tooltipText = null; _content = null; _view = null; _rows.Clear();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_watcher != null) _watcher.Owner = null; _watcher = null;
        ClearView(); _service.SetAvailable(false);
    }
    private sealed class Row
    {
        internal readonly Guid Token; internal readonly Button Button; internal readonly TMP_Text Label; internal readonly ForgeActionHover Hover;
        internal ForgeViewHandle? View; internal long Revision; internal Image? Icon;
        internal Row(Guid token, Button button, TMP_Text label, ForgeActionHover hover) { Token = token; Button = button; Label = label; Hover = hover; }
    }
}
internal sealed class ForgeUiWatcher : MonoBehaviour
{
    internal ForgeUiRuntime? Owner;
    private void OnDisable() => Owner?.Observe();
    private void OnDestroy() => Owner?.Observe();
}
internal sealed class ForgeActionHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    internal string Tooltip = "";
    internal Action<string>? Show;
    public void OnPointerEnter(PointerEventData eventData) => Show?.Invoke(Tooltip);
    public void OnPointerExit(PointerEventData eventData) => Show?.Invoke("");
    private void OnDisable() => Show?.Invoke("");
}
