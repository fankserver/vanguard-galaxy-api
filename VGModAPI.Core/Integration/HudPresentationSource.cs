using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VGModAPI.Runtime;

/// <summary>Explicit UI rendering, not a recipe data query. Native recipe icons may build previews.</summary>
internal sealed class HudPresentationSource
{
    private readonly Assembly _assembly;
    private readonly IReadOnlyDictionary<string, MethodInfo> _methods;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal HudPresentationSource(Assembly assembly, IReadOnlyDictionary<string, MethodInfo> methods) { _assembly = assembly; _methods = methods; }
    internal (string Name, object? Icon, object? TooltipItem) Resolve(HudPresentation request)
    {
        if (request.ProviderId != "vanilla") return (request.LocalId + " (unavailable)", null, null);
        if (request.Kind == HudPresentationKind.RefinedMaterial)
        {
            var type = _assembly.GetType("Source.Item.RefinedMaterial", true)!;
            if (!Enum.TryParse(type, request.LocalId, out var material) || material == null || !Enum.IsDefined(type, material) || material.ToString() != request.LocalId)
                return (request.LocalId + " (unavailable)", null, null);
            return ((string)_methods["hudMaterialName"].Invoke(null, new[] { material })!, _methods["hudMaterialIcon"].Invoke(null, new[] { material }), null);
        }
        var recipes = request.Kind == HudPresentationKind.ForgeRecipe;
        var typeName = recipes ? "Behaviour.Crafting.CraftingRecipe" : "Behaviour.Item.InventoryItemType";
        if (recipes && !request.LocalId.StartsWith("forge/", StringComparison.Ordinal)) return (request.LocalId + " (unavailable)", null, null);
        var id = recipes ? request.LocalId.Substring(6) : request.LocalId;
        var typeInfo = _assembly.GetType(typeName, true)!;
        var all = typeInfo.GetField("all", Flags) is FieldInfo field ? field.GetValue(null) : typeInfo.GetProperty("all", Flags)!.GetValue(null);
        var candidates = Scan(all).ToList();
        if (recipes)
            foreach (var parent in candidates.ToArray())
            {
                candidates.AddRange(Scan(RecipeCatalogNativeSource.Member(parent, "subRecipes")));
                if (candidates.Count > 16384) throw new InvalidOperationException("Presentation registry exceeds bounds.");
            }
        var matches = candidates.Distinct(NativeIdentity.Instance).Where(item => Equals(RecipeCatalogNativeSource.Member(item, "identifier"), id)).Take(2).ToArray();
        if (matches.Length != 1) return (request.LocalId + " (unavailable)", null, null);
        var item = matches[0]; var text = (string)RecipeCatalogNativeSource.Member(item, "displayName")!;
        var name = (string)_methods["hudTranslate"].Invoke(null, new object[] { text, Array.Empty<object>() })!;
        return (name, RecipeCatalogNativeSource.Member(item, "icon"), recipes ? null : item);
    }
    private static IEnumerable<object> Scan(object? values)
    {
        if (values is not IEnumerable sequence) throw new InvalidOperationException("Presentation registry unavailable.");
        var count = 0;
        foreach (var value in sequence)
        { if (++count > 16384) throw new InvalidOperationException("Presentation registry exceeds bounds."); if (value != null) yield return value; }
    }
    private sealed class NativeIdentity : IEqualityComparer<object>
    {
        internal static readonly NativeIdentity Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
