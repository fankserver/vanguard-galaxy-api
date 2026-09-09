using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class HudRuntime
{
    private readonly Dictionary<Guid, View> _views = new();
    private string _structure = "";
    private TMP_Text? _plainTooltip;
    private void Render()
    {
        var entries = _service.Entries.Where(entry => entry.Button != null || entry.Panel != null).ToArray();
        if (entries.Length == 0) { ClearContent(); return; }
        var session = _session();
        var structure = ((RectTransform)_canvas!.transform).rect.size.ToString() + string.Join(";",  entries.Select(entry => entry.Token.ToString("N") + (entry.Button != null ? "b" : "") +
            (entry.Panel == null ? "" : "p" + entry.Panel.Closable + string.Join("", entry.Panel.Rows.Select(row => row.Id.Length + ":" + row.Id)))));
        if (_root == null || structure != _structure)
        {
            ClearContent(); _structure = structure;
            _root = new GameObject("Mod API shared HUD", typeof(RectTransform));
            var root = (RectTransform)_root.transform; root.SetParent(_canvas!.transform, false); Stretch(root);
            var canvasHeight = ((RectTransform)_canvas.transform).rect.height;
            var panelHeight = Mathf.Clamp(canvasHeight - 390, 120, 360);
            var buttons = entries.Count(entry => entry.Button != null && entry.Panel == null);
            var panels = entries.Count(entry => entry.Panel != null);
            var buttonContent = Scroll(root, "Buttons", 340, 28, buttons * 124, false);
            var panelContent = Scroll(root, "Panels", buttons > 0 ? 372 : 340, panelHeight, panels * 308, false);
            buttonContent.parent.gameObject.SetActive(buttons != 0); panelContent.parent.gameObject.SetActive(panels != 0);
            var buttonIndex = 0; var panelIndex = 0;
            foreach (var entry in entries)
            {
                var view = new View(); _views.Add(entry.Token, view);
                if (entry.Button != null && entry.Panel == null)
                {
                    var rect = Box(buttonContent, "Button", buttonIndex++ * 124, 0, 120, 28, false);
                    view.Button = Button(rect, () => view.Revision, revision => Click(entry.Token, revision, HudInteractionKind.Button, null));
                    view.ButtonLabel = Label(rect, 12); view.ButtonHover = Hover(rect.gameObject);
                }
                if (entry.Panel == null) continue;
                var layout = new RecipeWidgetLayout(entry.Panel.Rows.Count, entry.Button != null, panelHeight);
                var panel = Box(panelContent, "Panel", panelIndex++ * 308, 0, RecipeWidgetLayout.Width, layout.Height, false);
                if (entry.Button != null)
                {
                    var footer = Box(panel, "Action", 6, 4, 288, 26, false);
                    view.Button = Button(footer, () => view.Revision, revision => Click(entry.Token, revision, HudInteractionKind.Button, null));
                    view.ButtonLabel = Label(footer, 12); view.ButtonHover = Hover(footer.gameObject);
                }
                panel.gameObject.AddComponent<Image>().color = new Color(.035f, .05f, .075f, .94f);
                var header = Box(panel, "Header", 6, -4, 258, 30, true);
                var headerHit = header.gameObject.AddComponent<Image>(); headerHit.color = Color.clear; headerHit.raycastTarget = true;
                view.Header = Display(header);
                if (entry.Panel.Closable)
                {
                    var close = Box(panel, "Close", 272, -6, 22, 22, true);
                    Button(close, () => view.Revision, revision => Click(entry.Token, revision, HudInteractionKind.ClosePanel, null)); Label(close, 13).text = "×";
                }
                var rows = Scroll(panel, "Rows", entry.Button != null ? 36 : 6, layout.RowsHeight, 0, true, entry.Panel.Rows.Count * RecipeWidgetLayout.RowHeight);
                foreach (var row in entry.Panel.Rows)
                {
                    var rect = Box(rows, "Row", 0, -view.Rows.Count * RecipeWidgetLayout.RowHeight, 286, RecipeWidgetLayout.RowHeight - 2, true);
                    var display = Display(rect); display.Button = Button(rect, () => view.Revision, revision => Click(entry.Token, revision, HudInteractionKind.Row, row.Id));
                    view.Rows.Add(row.Id, display);
                }
            }
            var tipRect = Box(root, "HUD tooltip", -420, -80, 400, 84, true);
            tipRect.anchorMin = tipRect.anchorMax = new Vector2(1, 1);
            var background = tipRect.gameObject.AddComponent<Image>(); background.color = new Color(.02f, .03f, .05f, .98f); background.raycastTarget = false;
            _plainTooltip = Label(tipRect, 12); _plainTooltip.alignment = TextAlignmentOptions.TopLeft; tipRect.gameObject.SetActive(false);
        }
        foreach (var entry in entries)
        {
            var view = _views[entry.Token];
            if (view.Revision == entry.Revision) continue;
            view.Revision = entry.Revision;
            if (entry.Button != null)
            {
                view.Button!.interactable = entry.Button.Enabled; view.ButtonLabel!.text = GameText(entry.Button.Label);
                view.ButtonHover!.Tooltip = entry.Button.Tooltip;
            }
            if (entry.Panel == null) continue;
            SafeBind(entry.Plugin, view.Header!, entry.Panel.Title, "", "", entry.Panel.Presentation);
            foreach (var row in entry.Panel.Rows)
            {
                var display = view.Rows[row.Id]; display.Button!.interactable = row.Clickable;
                SafeBind(entry.Plugin, display, row.Label, row.Detail, row.Tooltip, row.Presentation);
                display.Amount.gameObject.SetActive(row.IngredientAmounts != null);
                display.Text.rectTransform.offsetMax = new Vector2(row.IngredientAmounts != null ? -84 : -4, 0);
                if (row.IngredientAmounts is { } amounts)
                {
                    display.Text.text += " x" + amounts.RequiredText;
                    display.Amount.text = "(" + amounts.AvailableText + ")";
                    display.Amount.color = amounts.Sufficient == true ? Color.green : amounts.Sufficient == false ? new Color(1, .25f, .25f) : Color.gray;
                }
            }
        }
        if (_session() != session) ClearSurface();
    }
    private void SafeBind(string provider, DisplayRow row, string label, string detail, string tooltip, HudPresentation? presentation)
    {
        try { Bind(row, label, detail, tooltip, presentation); }
        catch (Exception error)
        {
            row.Text.text = GameText((label.Length == 0 ? presentation?.LocalId + " (unavailable)" : label) + "  " + detail);
            row.Text.color = Color.white;
            row.Icon.sprite = null; row.Icon.gameObject.SetActive(false); row.Hover.Tooltip = tooltip;
            var native = row.Text.transform.parent.GetComponent(_assembly.GetType("Behaviour.UI.Tooltip.ItemTooltipSource", true)!) as UnityEngine.Behaviour;
            if (native != null) native.enabled = false;
            try { _report(new InvalidOperationException("HUD presentation failed for " + provider, error)); } catch { }
        }
    }
    private void Bind(DisplayRow row, string label, string detail, string tooltip, HudPresentation? presentation)
    {
        var resolved = presentation == null ? (Name: "", Icon: (object?)null, TooltipItem: (object?)null) : _presentation.Resolve(presentation);
        row.Text.text = GameText((label.Length == 0 ? resolved.Name : label) + (detail.Length == 0 ? "" : "  " + detail));
        row.Text.color = _presentation.ItemColor(resolved.TooltipItem) is Color color ? color : Color.white;
        row.Icon.sprite = resolved.Icon as Sprite; row.Icon.gameObject.SetActive(row.Icon.sprite != null);
        row.Text.rectTransform.offsetMin = new Vector2(row.Icon.sprite != null ? 28 : 4, 0);
        var tooltipType = _assembly.GetType("Behaviour.UI.Tooltip.ItemTooltipSource", true)!;
        var native = row.Text.transform.parent.GetComponent(tooltipType) as UnityEngine.Behaviour;
        var parentType = _assembly.GetType("Behaviour.UI.Tooltip.UITooltipParent", true)!;
        var prefab = parentType.GetProperty("ItemTooltipPrefab")!.GetValue(null) as UnityEngine.Object;
        var contextReady = prefab != null && UnityEngine.Object.FindObjectsByType(parentType, FindObjectsInactive.Exclude).Length == 1;
        if (resolved.TooltipItem != null && contextReady)
        {
            native ??= (UnityEngine.Behaviour)row.Text.transform.parent.gameObject.AddComponent(tooltipType);
            var context = Enum.Parse(_assembly.GetType("Behaviour.UI.Tooltip.ItemTooltipContext", true)!, "CraftingPreview");
            _methods["hudItemTooltip"].Invoke(native, new object?[] { resolved.TooltipItem, presentation!.TooltipCount, false, context, false, null });
            row.Hover.Tooltip = "";
        }
        else { if (native != null) native.enabled = false; row.Hover.Tooltip = tooltip; }
    }
    private ForgeActionHover Hover(GameObject go)
    {
        var hover = go.AddComponent<ForgeActionHover>();
        hover.Show = text => { if (_plainTooltip != null) { _plainTooltip.text = GameText(text); _plainTooltip.transform.parent.gameObject.SetActive(text.Length != 0); } };
        return hover;
    }
    private DisplayRow Display(RectTransform rect)
    {
        var icon = new GameObject("Icon", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        var imageRect = (RectTransform)icon.transform; imageRect.SetParent(rect, false); imageRect.anchorMin = imageRect.anchorMax = new Vector2(0, .5f);
        imageRect.pivot = new Vector2(0, .5f); imageRect.anchoredPosition = new Vector2(3, 0); imageRect.sizeDelta = new Vector2(24, 24);
        icon.preserveAspect = true; icon.raycastTarget = false;
        var amount = Label(rect, 12); amount.alignment = TextAlignmentOptions.MidlineRight;
        amount.rectTransform.anchorMin = new Vector2(1, 0); amount.rectTransform.offsetMin = new Vector2(-82, 0);
        amount.gameObject.SetActive(false);
        return new DisplayRow(Label(rect, 12), amount, icon, Hover(rect.gameObject));
    }
    private static string GameText(string value) => value.Replace('\u00b7', '-').Replace("\u2026", "...");
    private TMP_Text Label(RectTransform parent, int size)
    {
        var text = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>(); text.transform.SetParent(parent, false);
        Stretch(text.rectTransform); text.rectTransform.offsetMin = new Vector2(4, 0); text.rectTransform.offsetMax = new Vector2(-4, 0);
        text.font = _font; text.fontSize = size; text.richText = false; text.raycastTarget = false;
        text.alignment = TextAlignmentOptions.MidlineLeft; text.overflowMode = TextOverflowModes.Truncate; return text;
    }
    private static Button Button(RectTransform rect, Func<long> revision, Action<long> callback)
    {
        var image = rect.gameObject.AddComponent<Image>(); image.color = new Color(.09f, .13f, .18f, .95f);
        var button = rect.gameObject.AddComponent<RevisionButton>(); button.ReadRevision = revision;
        button.targetGraphic = image; button.onClick.AddListener(() => callback(button.InvocationRevision)); return button;
    }
    private static RectTransform Box(RectTransform parent, string name, float x, float y, float width, float height, bool top)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0, top ? 1 : 0); rect.pivot = new Vector2(0, top ? 1 : 0);
        rect.anchoredPosition = new Vector2(x, y); rect.sizeDelta = new Vector2(width, height); return rect;
    }
    private static RectTransform Scroll(RectTransform parent, string name, float bottom, float height, float width, bool vertical, float contentHeight = 0)
    {
        var rect = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D)).GetComponent<RectTransform>();
        rect.SetParent(parent, false); rect.anchorMin = Vector2.zero; rect.anchorMax = new Vector2(1, 0); rect.pivot = new Vector2(.5f, 0);
        rect.offsetMin = new Vector2(6, bottom); rect.offsetMax = new Vector2(-6, bottom + height);
        if (!vertical)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(1, 0); rect.pivot = new Vector2(1, 0);
            rect.anchoredPosition = new Vector2(-6, bottom); rect.sizeDelta = new Vector2(Mathf.Min(Mathf.Max(0, width), Mathf.Max(0, parent.rect.width - 12)), height);
        }
        rect.GetComponent<Image>().color = new Color(0, 0, 0, .01f);
        var content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>(); content.SetParent(rect, false);
        content.anchorMin = new Vector2(0, vertical ? 1 : 0); content.anchorMax = new Vector2(vertical ? 1 : 0, 1);
        content.pivot = new Vector2(0, 1); content.sizeDelta = new Vector2(vertical ? 0 : Mathf.Max(0, width), vertical ? contentHeight : 0);
        var scroll = rect.GetComponent<ScrollRect>(); scroll.viewport = rect; scroll.content = content;
        scroll.horizontal = !vertical; scroll.vertical = vertical; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 24;
        return content;
    }
    private static void Stretch(RectTransform rect) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero; }
    private sealed class View
    {
        internal long Revision = -1;
        internal Button? Button; internal TMP_Text? ButtonLabel; internal ForgeActionHover? ButtonHover; internal DisplayRow? Header;
        internal readonly Dictionary<string, DisplayRow> Rows = new(StringComparer.Ordinal);
    }
    private sealed class DisplayRow
    {
        internal readonly TMP_Text Text; internal readonly TMP_Text Amount; internal readonly Image Icon; internal readonly ForgeActionHover Hover; internal Button? Button;
        internal DisplayRow(TMP_Text text, TMP_Text amount, Image icon, ForgeActionHover hover) { Text = text; Amount = amount; Icon = icon; Hover = hover; }
    }
}
