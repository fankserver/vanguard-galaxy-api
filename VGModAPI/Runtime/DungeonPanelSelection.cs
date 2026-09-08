using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
namespace VGModAPI.Runtime;

/// <summary>Keep keyboard/controller selection visible without changing native panel selection.</summary>
internal sealed class DungeonPanelSelection : MonoBehaviour, ISelectHandler
{
    internal ScrollRect? Scroll;
    public void OnSelect(BaseEventData eventData)
    {
        if (!Scroll || !Scroll!.content || !Scroll.viewport || transform is not RectTransform item) return;
        Canvas.ForceUpdateCanvases();
        var corners = new Vector3[4]; item.GetWorldCorners(corners);
        var bottom = Scroll.viewport.InverseTransformPoint(corners[0]).y;
        var top = Scroll.viewport.InverseTransformPoint(corners[1]).y;
        var bounds = Scroll.viewport.rect;
        var delta = top > bounds.yMax ? bounds.yMax - top : bottom < bounds.yMin ? bounds.yMin - bottom : 0;
        if (Mathf.Approximately(delta, 0)) return;
        Scroll.StopMovement();
        var position = Scroll.content.anchoredPosition;
        position.y = Mathf.Clamp(position.y + delta, 0, Mathf.Max(0, Scroll.content.rect.height - bounds.height));
        Scroll.content.anchoredPosition = position;
    }
}
