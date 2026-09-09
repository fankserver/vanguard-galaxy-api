using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class HudRuntime
{
    private bool BuildLaunchers(RectTransform root)
    {
        var bottom = false;
        foreach (HudCorner corner in Enum.GetValues(typeof(HudCorner)))
        {
            var entries = _service.Launchers(corner);
            var layout = new HudLauncherLayout(corner,
                entries.Select(entry => entry.Button!.Icon.HasValue ? HudLauncherLayout.IconWidth : HudLauncherLayout.TextWidth),
                root.rect.width, root.rect.height);
            if (!layout.Visible) continue;
            bottom |= corner is HudCorner.BottomLeft or HudCorner.BottomRight;
            var content = LauncherStrip(root, corner, layout);
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index]; var slot = layout.Slots[index];
                var rect = Box(content, "Launcher", slot.X, 0, slot.Width, HudLauncherLayout.RowHeight, true);
                var view = new View { IsLauncher = true };
                _views.Add(entry.Token, view);
                view.Button = Button(rect, () => view.Revision, revision => Click(entry.Token, revision, HudInteractionKind.Button, null));
                view.ButtonLabel = Label(rect, 12); view.ButtonLabel.alignment = TextAlignmentOptions.Center;
                if (entry.Button!.Icon.HasValue)
                {
                    view.ButtonLabel.enableAutoSizing = true;
                    view.ButtonLabel.fontSizeMin = 8; view.ButtonLabel.fontSizeMax = 12;
                }
                view.ButtonIcon = LauncherIcon(rect, true);
                view.ButtonHover = Hover(rect.gameObject);
            }
        }
        return bottom;
    }

    private static RectTransform LauncherStrip(RectTransform root, HudCorner corner, HudLauncherLayout layout)
    {
        var rect = new GameObject("Launchers " + corner, typeof(RectTransform), typeof(ScrollRect)).GetComponent<RectTransform>();
        rect.SetParent(root, false); rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(layout.X, layout.Y); rect.sizeDelta = new Vector2(layout.Width, layout.Height);
        var viewport = Box(rect, "Viewport", 0, layout.Top && layout.Overflows ? HudLauncherLayout.ScrollbarSpace : 0,
            layout.Width, HudLauncherLayout.RowHeight, false);
        viewport.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .01f);
        viewport.gameObject.AddComponent<RectMask2D>();
        var content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
        content.SetParent(viewport, false); content.anchorMin = content.anchorMax = content.pivot = new Vector2(0, 1);
        content.sizeDelta = new Vector2(layout.ContentWidth, HudLauncherLayout.RowHeight);
        var scroll = rect.GetComponent<ScrollRect>(); scroll.content = content; scroll.viewport = viewport;
        scroll.horizontal = layout.Overflows; scroll.vertical = false;
        scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 48;
        if (layout.Overflows)
        {
            var track = Box(rect, "Scroll", 0, layout.Top ? 0 : HudLauncherLayout.RowHeight + 2, layout.Width, 6, false);
            track.gameObject.AddComponent<Image>().color = new Color(.04f, .06f, .09f, .9f);
            var handle = Box(track, "Handle", 0, 0, 0, 0, false); Stretch(handle);
            var graphic = handle.gameObject.AddComponent<Image>(); graphic.color = new Color(.3f, .5f, .65f, .9f);
            var bar = track.gameObject.AddComponent<Scrollbar>(); bar.handleRect = handle; bar.targetGraphic = graphic;
            bar.direction = Scrollbar.Direction.LeftToRight; scroll.horizontalScrollbar = bar;
        }
        scroll.horizontalNormalizedPosition = layout.Right ? 1 : 0;
        return content;
    }

    private static Image LauncherIcon(RectTransform parent, bool launcher)
    {
        var image = new GameObject("Visual", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        image.enabled = false; image.raycastTarget = false; image.preserveAspect = true;
        var rect = (RectTransform)image.transform; rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(launcher ? .5f : 0, .5f);
        rect.anchoredPosition = new Vector2(launcher ? 0 : 4, 0);
        rect.sizeDelta = launcher ? new Vector2(32, 32) : new Vector2(22, 22);
        return image;
    }

    private void BindButton(View view, HudButton model)
    {
        view.Button!.interactable = model.Enabled;
        view.ButtonHover!.Tooltip = model.Tooltip.Length == 0 ? model.Label : model.Tooltip;
        view.ButtonLabel!.text = GameText(model.Label);
        var visual = model.Icon.HasValue ? _icons.Read(model.Icon.Value, Time.unscaledTime) : default;
        var sprite = model.Icon.HasValue ? visual.Icon : null;
        // A spriteless Image draws a solid white rectangle. Never enable it until resolution succeeds.
        view.ButtonIcon!.enabled = sprite != null;
        view.ButtonIcon.sprite = sprite;
        view.ButtonIcon.color = model.Enabled ? Color.white : new Color(1, 1, 1, .4f);
        view.ButtonLabel.gameObject.SetActive(!view.IsLauncher || !model.Icon.HasValue || visual.State == HudIconState.LabelFallback);
        if (!view.IsLauncher) view.ButtonLabel.rectTransform.offsetMin = new Vector2(sprite != null ? 30 : 4, 0);
    }
}
