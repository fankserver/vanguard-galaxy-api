using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class PickupPresentationRuntime
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly PickupPresentationService _service;
    private readonly Action<Exception> _report;
    private readonly PropertyInfo _identifier, _name, _rarity;
    private readonly MethodInfo _rarityColor;
    private readonly FieldInfo _text, _fadeColor;
    private readonly object _standard, _pickup;
    private readonly PickupPresentationScope _scope = new();
    internal MethodInfo Notify { get; }
    internal MethodInfo Show { get; }
    internal PickupPresentationRuntime(Assembly assembly, PickupPresentationService service, Action<Exception> report)
    {
        _service = service; _report = report;
        var item = assembly.GetType("Behaviour.Item.InventoryItemType", true)!;
        var rarity = assembly.GetType("Source.Item.Rarity", true)!;
        var floating = assembly.GetType("Behaviour.UI.FloatingInfoText", true)!;
        var infoType = assembly.GetType("Behaviour.UI.InfoType", true)!;
        _identifier = Property(item, "identifier", typeof(string));
        _name = Property(item, "displayName", typeof(string));
        _rarity = Property(item, "rarity", rarity);
        _text = Field(floating, "numberText", typeof(TextMeshPro));
        _fadeColor = Field(floating, "textColor", typeof(Color));
        _rarityColor = assembly.GetType("Source.Util.RarityExtensions", true)!.GetMethod("GetColor", new[] { rarity })
            ?? throw new MissingMethodException("RarityExtensions.GetColor");
        if (_rarityColor.ReturnType != typeof(Color) || !_rarityColor.IsStatic) throw new InvalidOperationException("Rarity color binding changed.");
        _standard = Enum.Parse(rarity, "Standard"); _pickup = Enum.Parse(infoType, "PICKUP");
        Notify = assembly.GetType("Source.Data.AbstractUnitData", true)!.GetMethod("NotifyItemPickup", Flags, null, new[] { item, typeof(int) }, null)
            ?? throw new MissingMethodException("AbstractUnitData.NotifyItemPickup");
        Show = floating.GetMethod("Show", Flags, null,
            new[] { typeof(GameObject), typeof(float), infoType, typeof(Vector2), typeof(string), typeof(Color?) }, null)
            ?? throw new MissingMethodException("FloatingInfoText.Show");
        if (Notify.ReturnType != typeof(void) || Show.ReturnType != typeof(void)) throw new InvalidOperationException("Pickup binding changed.");
    }
    private static PropertyInfo Property(Type owner, string name, Type expected)
    {
        var p = owner.GetProperty(name, Flags);
        if (p?.PropertyType != expected || p.GetGetMethod(true) == null) throw new MissingMemberException(owner.FullName, name);
        return p;
    }
    private static FieldInfo Field(Type owner, string name, Type expected)
    {
        var f = owner.GetField(name, Flags);
        if (f?.FieldType != expected) throw new MissingFieldException(owner.FullName, name);
        return f;
    }
    internal IDisposable? Begin(object item, int count)
    {
        // Push even an unreadable frame: never inherit an outer item's tint.
        var frame = _scope.Begin();
        if (frame == null) return null;
        try
        {
            var rarity = _rarity.GetValue(item)!;
            UiColor? color = null;
            if (!rarity.Equals(_standard))
            {
                var native = (Color)_rarityColor.Invoke(null, new[] { rarity })!;
                color = new UiColor(native.r, native.g, native.b, native.a);
            }
            frame.Pickup = new ItemPickupPresentation((string)_identifier.GetValue(item)!, (string)_name.GetValue(item)!, count, color);
        }
        catch (Exception error) { Report(error); }
        return frame;
    }
    internal void Apply(object floating, object type, string postfix)
    {
        try
        {
            var pickup = _scope.Consume(_pickup.Equals(type), postfix);
            if (pickup == null) return;
            var color = _service.Resolve(pickup);
            if (!color.HasValue || _text.GetValue(floating) is not TextMeshPro text || text == null) return;
            var c = color.Value;
            var native = new Color(c.Red, c.Green, c.Blue, c.Alpha);
            _fadeColor.SetValue(floating, native);
            text.color = native;
        }
        catch (Exception error) { Report(error); }
    }
    private void Report(Exception error) { try { _report(error); } catch { } }
}
