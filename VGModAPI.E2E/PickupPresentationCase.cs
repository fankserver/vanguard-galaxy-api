using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VGModAPI.E2E;

/// <summary>
/// Drives the real notify path (AbstractUnitData.NotifyItemPickup -> UIInfoTextParent ->
/// FloatingInfoText.Show) and verifies the typed pickup-presentation contract end to end:
/// a registered resolver retints the vanilla floating text, abstaining rules fall through to
/// the first non-null rule, later rules stay invisible, each notify consumes exactly one
/// scoped frame with no cross-item inheritance, and disposal restores the vanilla pickup
/// palette. All registrations are made and disposed by this case.
/// </summary>
internal static class PickupPresentationCase
{
    internal const string Id = "pickup-presentation";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly List<IDisposable> Rules = new();
    private static readonly Dictionary<string, int> Resolutions = new();
    private static readonly HashSet<string> UsedIdentifiers = new();

    private static Assembly Game => AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == "Assembly-CSharp");
    private static Type T(string name) => Game.GetType(name, true)!;
    private static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private static bool Close(Color a, Color b) => Mathf.Abs(a.r - b.r) < 1e-4f && Mathf.Abs(a.g - b.g) < 1e-4f && Mathf.Abs(a.b - b.b) < 1e-4f && Mathf.Abs(a.a - b.a) < 1e-4f;

    private static readonly Type ItemType = T("Behaviour.Item.InventoryItemType");
    private static readonly Type FloatingType = T("Behaviour.UI.FloatingInfoText");
    private static readonly Type UnitDataType = T("Source.Data.AbstractUnitData");
    private static PropertyInfo Prop(Type owner, string name) => owner.GetProperty(name, Any) ?? throw new MissingMemberException(owner.FullName, name);
    private static readonly PropertyInfo IdentifierProp = Prop(ItemType, "identifier");
    private static readonly PropertyInfo DisplayNameProp = Prop(ItemType, "displayName");
    private static readonly PropertyInfo RarityProp = Prop(ItemType, "rarity");
    private static readonly FieldInfo AllItemsField = ItemType.GetField("allItems", Any) ?? throw new MissingMemberException(ItemType.FullName, "allItems");
    private static readonly MethodInfo Notify = UnitDataType.GetMethods(Any).Single(m => m.Name == "NotifyItemPickup" && m.GetParameters().Length == 2);
    private static readonly FieldInfo NumberTextField = FloatingType.GetField("numberText", Any)!;
    private static readonly FieldInfo XpColorField = FloatingType.GetField("xpColor", Any)!;
    private static readonly FieldInfo DefaultColorField = FloatingType.GetField("defaultColor", Any)!;
    private static readonly MethodInfo Translate = T("Source.Util.Translation").GetMethod("Translate", new[] { typeof(string), typeof(object[]) })!;
    private static readonly object StandardRarity = Enum.Parse(RarityProp.PropertyType, "Standard");

    private static string TranslateName(object itemType) => (string)Translate.Invoke(null, new object[] { (string)DisplayNameProp.GetValue(itemType)!, Array.Empty<object>() })!;
    private static string IdOf(object itemType) => (string)IdentifierProp.GetValue(itemType)!;

    private static int _presentationPhase, _scopedPhase, _cleanupPhase;
    private static object[] _plannedItems = Array.Empty<object>();
    private static Color[] _sentinels = Array.Empty<Color>();
    private static UiColor[] _uiSentinels = Array.Empty<UiColor>();

    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events)
    {
        var steps = new List<TestStep>(LiveBoot.Steps(ModApi.PluginId, lifecycle, events));
        steps.Add(new TestStep("registers resolvers and waits for the pickup service", "IPickupPresentationService.Register", 30, Declare));
        steps.Add(new TestStep("tints a real notify and hides the shadowing later rule", "NotifyItemPickup / FloatingInfoText.Show", 30, Presentation));
        steps.Add(new TestStep("same-frame notifies stay scoped and resolve exactly once", "PickupPresentationScope.Consume", 30, ScopedResolution));
        steps.Add(new TestStep("disposal restores the vanilla pickup palette", "registered resolver disposal", 30, Cleanup));
        return steps;
    }

    private static IEnumerable<object> NonStandardItems() => ((IDictionary)AllItemsField.GetValue(null)!).Values.Cast<object>()
        .Where(t => !RarityProp.GetValue(t)!.Equals(StandardRarity) && (string?)IdentifierProp.GetValue(t) is string id && !UsedIdentifiers.Contains(id) && !string.IsNullOrEmpty((string?)DisplayNameProp.GetValue(t)));

    private static object TakeItem()
    {
        var item = NonStandardItems().FirstOrDefault() ?? throw new InvalidOperationException("game exposes no non-standard items to tint");
        UsedIdentifiers.Add(IdOf(item));
        return item;
    }

    private static void NotifyPlayer(object itemType, int count)
    {
        // AbstractUnitData is a plain class: reach the player's instance through the game's own accessors.
        var gm = T("GameplayManager").GetField("Instance", BindingFlags.Static | Any)!.GetValue(null);
        Require(gm != null, "gameplay manager not present");
        var ship = gm!.GetType().GetProperty("spaceShip", Any)?.GetValue(gm);
        Require(ship != null, "player space ship not present");
        object? data = null;
        for (Type? t = ship!.GetType(); t != null && data == null; t = t.BaseType)
            data = t.GetProperty("unitData", Any | BindingFlags.DeclaredOnly)?.GetValue(ship)
                ?? t.GetField("unitData", Any | BindingFlags.DeclaredOnly)?.GetValue(ship);
        Require(data != null, "player unit data not attached");
        Notify.Invoke(data, new object[] { itemType, count });
    }

    /// <summary>Finds the live float rendering "+{count} {translatedName}" and returns its number color.</summary>
    private static Color? FindFloat(object itemType, int count)
    {
        var expected = "+" + count + " " + TranslateName(itemType);
        foreach (var float_ in Resources.FindObjectsOfTypeAll(FloatingType))
        {
            if (NumberTextField.GetValue(float_) is not UnityEngine.Object text || text == null) continue;
            var label = (string?)text.GetType().GetProperty("text", Any)?.GetValue(text);
            if (label != expected) continue;
            return (Color)text.GetType().GetProperty("color", Any)!.GetValue(text)!;
        }
        return null;
    }

    private static bool AnyFloatHas(Color color) => Resources.FindObjectsOfTypeAll(FloatingType)
        .Any(f => NumberTextField.GetValue(f) is UnityEngine.Object text && text != null
            && Close((Color)text.GetType().GetProperty("color", Any)!.GetValue(text)!, color));

    private static StepResult Declare()
    {
        Require(ModApi.Services.PickupPresentation.Availability.IsAvailable, "pickup presentation service not bound");
        _plannedItems = Enumerable.Range(0, 5).Select(_ => TakeItem()).ToArray();
        _sentinels = _plannedItems.Select((_, k) => new Color(0.1f + 0.15f * k, 0.9f - 0.17f * k, 0.35f + 0.12f * k)).ToArray();
        _uiSentinels = _sentinels.Select(c => new UiColor(c.r, c.g, c.b, c.a)).ToArray();
        var wanted = new Dictionary<string, UiColor>();
        for (var k = 0; k < _plannedItems.Length; k++) wanted[IdOf(_plannedItems[k])] = _uiSentinels[k];
        for (var k = 0; k < _plannedItems.Length; k++)
            Rules.Add(ModApi.Services.PickupPresentation.Register("vgmodapi.e2e.pickup-main." + k, p =>
            {
                Resolutions[p.ItemId] = Resolutions.TryGetValue(p.ItemId, out var n) ? n + 1 : 1;
                return wanted.TryGetValue(p.ItemId, out var c) ? c : null;
            }));
        // An abstaining rule first and a shadowing rule last: only the middle resolvers answer.
        Rules.Insert(0, ModApi.Services.PickupPresentation.Register("vgmodapi.e2e.pickup-abstain", _ => null));
        Rules.Add(ModApi.Services.PickupPresentation.Register("vgmodapi.e2e.pickup-shadow",
            p => p.ItemId == IdOf(_plannedItems[0]) ? new UiColor(1f, 0f, 0f) : null));
        return StepResult.Pass("five per-item resolvers plus abstaining and shadowing rules registered");
    }

    private static StepResult Presentation()
    {
        if (_presentationPhase == 0) { NotifyPlayer(_plannedItems[0], 3); _presentationPhase = 1; }
        var seen = FindFloat(_plannedItems[0], 3);
        if (seen == null) return StepResult.Wait("waiting for the tinted pickup float");
        Require(Close(seen.Value, _sentinels[0]), "float color " + seen + " is not the registered resolver color");
        Require(!AnyFloatHas(new Color(1f, 0f, 0f)), "the later shadowing rule overwrote the first non-null result");
        Require(Resolutions.TryGetValue(IdOf(_plannedItems[0]), out var hits) && hits == 1,
            "resolver consulted " + hits + " times for a single notify");
        return StepResult.Pass("registered resolver tinted the native float; abstaining and shadowing rules behaved");
    }

    private static StepResult ScopedResolution()
    {
        if (_scopedPhase == 0) { NotifyPlayer(_plannedItems[1], 1); NotifyPlayer(_plannedItems[2], 7); NotifyPlayer(_plannedItems[3], 2); _scopedPhase = 1; }
        var counts = new[] { 1, 7, 2 };
        for (var k = 1; k <= 3; k++)
        {
            var seen = FindFloat(_plannedItems[k], counts[k - 1]);
            if (seen == null) return StepResult.Wait("waiting for scoped float " + k);
            Require(Close(seen.Value, _sentinels[k]), "scoped float " + k + " shows another registration's color");
            Require(Resolutions.TryGetValue(IdOf(_plannedItems[k]), out var hits) && hits == 1, "scoped item " + k + " resolved " + hits + " times");
        }
        return StepResult.Pass("three same-frame notifies each resolved exactly once with their own color");
    }

    private static StepResult Cleanup()
    {
        if (_cleanupPhase == 0)
        {
            foreach (var rule in Rules) rule.Dispose();
            Rules.Clear();
            NotifyPlayer(_plannedItems[4], 9);
            _cleanupPhase = 1;
        }
        var seen = FindFloat(_plannedItems[4], 9);
        if (seen == null) return StepResult.Wait("waiting for the untinted pickup float after disposal");
        // With no resolver the game shows its serialized PICKUP palette (xpColor on Show).
        var instance = Resources.FindObjectsOfTypeAll(FloatingType).First(f =>
            NumberTextField.GetValue(f) is UnityEngine.Object text && text != null
            && (string?)text.GetType().GetProperty("text", Any)?.GetValue(text) == "+" + 9 + " " + TranslateName(_plannedItems[4]));
        var vanilla = (Color)XpColorField.GetValue(instance)!;
        Require(Close(seen.Value, vanilla), "float kept a resolver color after disposal: " + seen);
        Require(!Resolutions.ContainsKey(IdOf(_plannedItems[4])), "resolvers consulted after disposal");
        return StepResult.Pass("teardown restored the vanilla pickup presentation");
    }
}
