using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VGModAPI.Core;
namespace VGModAPI.Runtime;

internal sealed class DungeonPanelView : IDisposable
{
    private readonly DungeonPanelRuntime _source;
    private readonly DungeonPanelService _service;
    private readonly List<Entry> _entries = new();
    private GameObject? _root;
    private RectTransform? _content;
    private Guid _view;
    private sealed class Entry
    {
        internal readonly GameObject Object;
        internal readonly TMP_Text Text;
        internal readonly Button? Button;
        internal DungeonPanelService.Row Row;
        internal Entry(GameObject obj, TMP_Text text, Button? button, DungeonPanelService.Row row) { Object = obj; Text = text; Button = button; Row = row; }
    }
    internal DungeonPanelView(DungeonPanelRuntime source, DungeonPanelService service) { _source = source; _service = service; }
    internal void Tick()
    {
        var snapshot = _service.Current;
        if (snapshot == null) { Clear(); return; }
        var rows = _service.Render(); if (rows.Count == 0) { Clear(); return; }
        var anchor = _source.PanelRect; var font = _source.PanelFont;
        if (!anchor || !font) { Clear(); return; }
        if (!_root || _view != snapshot.ViewId)
        {
            Clear(); _view = snapshot.ViewId;
            _root = new GameObject("Mod API dungeon contributions", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            var rect = (RectTransform)_root.transform; rect.SetParent(anchor, false); rect.anchorMin = rect.anchorMax = new Vector2(1, 1); rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(8, 0); rect.sizeDelta = new Vector2(280, Mathf.Min(360, Mathf.Max(120, anchor!.rect.height)));
            _root.GetComponent<Image>().color = new Color(.025f, .035f, .05f, .97f);
            _content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter)).GetComponent<RectTransform>();
            _content.SetParent(rect, false); _content.anchorMin = new Vector2(0, 1); _content.anchorMax = new Vector2(1, 1); _content.pivot = new Vector2(.5f, 1); _content.sizeDelta = Vector2.zero;
            var layout = _content.GetComponent<VerticalLayoutGroup>(); layout.padding = new RectOffset(8, 8, 8, 8); layout.spacing = 6; layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            _content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = _root.GetComponent<ScrollRect>(); scroll.viewport = rect; scroll.content = _content; scroll.horizontal = false; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 28;
        }
        var canvas = anchor!.GetComponentInParent<Canvas>()?.rootCanvas.transform as RectTransform;
        if (!canvas) { Clear(); return; }
        var corners = new Vector3[4]; anchor.GetWorldCorners(corners);
        var lower = canvas!.InverseTransformPoint(corners[0]); var upper = canvas.InverseTransformPoint(corners[2]);
        var bounds = canvas.rect;
        var placement = DungeonPanelPlacement.Around(bounds.xMin + 8, bounds.yMin + 8, bounds.xMax - 8, bounds.yMax - 8, lower.x, lower.y, upper.x, upper.y);
        _root!.SetActive(placement.HasValue);
        if (!placement.HasValue) return;
        var area = placement.Value; var placed = (RectTransform)_root.transform;
        placed.position = canvas.TransformPoint(new Vector3(area.X, area.Top, 0));
        var size = anchor.InverseTransformVector(canvas.TransformVector(new Vector3(area.Width, area.Height, 0)));
        placed.sizeDelta = new Vector2(Mathf.Abs(size.x), Mathf.Abs(size.y));
        var rebuild = _entries.Count != rows.Count;
        for (var i = 0; !rebuild && i < rows.Count; i++) rebuild = _entries[i].Row.Registration != rows[i].Registration || (_entries[i].Button != null) != (rows[i].Action != null);
        if (rebuild)
        {
            foreach (var entry in _entries) { entry.Object.SetActive(false); UnityEngine.Object.Destroy(entry.Object); } _entries.Clear();
            foreach (var row in rows)
            {
                var obj = new GameObject("Contribution", typeof(RectTransform), typeof(VerticalLayoutGroup)); obj.transform.SetParent(_content, false);
                var layout = obj.GetComponent<VerticalLayoutGroup>(); layout.padding = new RectOffset(6, 6, 6, 6); layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandHeight = false;
                Button? button = null;
                if (row.Action != null) { var image = obj.AddComponent<Image>(); image.color = new Color(.08f, .18f, .23f, 1); button = obj.AddComponent<Button>(); button.targetGraphic = image; }
                var text = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>(); text.transform.SetParent(obj.transform, false); text.font = font; text.fontSize = 14; text.richText = false; text.raycastTarget = false; text.textWrappingMode = TextWrappingModes.Normal;
                var entry = new Entry(obj, text, button, row); _entries.Add(entry);
                if (button != null)
                {
                    obj.AddComponent<DungeonPanelSelection>().Scroll = _root.GetComponent<ScrollRect>();
                    button.onClick.AddListener(() => _service.Activate(entry.Row.Registration, entry.Row.Snapshot.ViewId, entry.Row.Snapshot.Revision));
                }
            }
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var entry = _entries[i]; entry.Row = rows[i];
            entry.Text.text = rows[i].Section is { } section ? section.Title + "\n" + section.Text : rows[i].Action!.Label + (rows[i].Action!.Tooltip.Length == 0 ? "" : "\n" + rows[i].Action!.Tooltip);
            if (entry.Button != null) entry.Button.interactable = rows[i].Action!.Enabled;
        }
    }
    private void Clear() { if (_root) { _root!.SetActive(false); UnityEngine.Object.Destroy(_root); } _root = null; _content = null; _entries.Clear(); }
    public void Dispose() => Clear();
}
