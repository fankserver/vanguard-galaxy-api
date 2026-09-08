using System;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VGModAPI.Runtime;

/// <summary>Keep pointer-down intent when a model changes before release; keyboard submission uses the current model.</summary>
internal sealed class RevisionButton : Button
{
    internal Func<long>? ReadRevision;
    internal long InvocationRevision { get; private set; }
    private long? _pressed;
    public override void OnPointerDown(PointerEventData eventData)
    {
        if (eventData.button == PointerEventData.InputButton.Left) _pressed = ReadRevision?.Invoke();
        base.OnPointerDown(eventData);
    }
    public override void OnPointerClick(PointerEventData eventData)
    {
        InvocationRevision = _pressed ?? ReadRevision?.Invoke() ?? -1;
        try { base.OnPointerClick(eventData); } finally { _pressed = null; }
    }
    public override void OnSubmit(BaseEventData eventData)
    {
        InvocationRevision = ReadRevision?.Invoke() ?? -1;
        base.OnSubmit(eventData);
    }
    protected override void OnDisable() { _pressed = null; base.OnDisable(); }
}
