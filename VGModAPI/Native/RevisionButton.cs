using System;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Keep pointer-down intent when a model changes before release; keyboard submission uses the current model.</summary>
internal sealed class RevisionButton : Button
{
    internal Func<long>? ReadRevision;
    internal long InvocationRevision { get; private set; }
    private readonly PointerRevision _press = new();
    public override void OnPointerDown(PointerEventData eventData)
    {
        if (eventData.button == PointerEventData.InputButton.Left) _press.Down(true, IsActive() && IsInteractable() ? ReadRevision?.Invoke() : null);
        base.OnPointerDown(eventData);
    }
    public override void OnPointerClick(PointerEventData eventData)
    {
        var revision = _press.Click(eventData.button == PointerEventData.InputButton.Left);
        if (!revision.HasValue) return;
        InvocationRevision = revision.Value;
        base.OnPointerClick(eventData);
    }
    public override void OnSubmit(BaseEventData eventData)
    {
        InvocationRevision = ReadRevision?.Invoke() ?? -1;
        base.OnSubmit(eventData);
    }
    protected override void OnDisable() { _press.Clear(); base.OnDisable(); }
}
