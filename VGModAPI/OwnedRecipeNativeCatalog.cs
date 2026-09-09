using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VGModAPI.Core;

namespace VGModAPI;
internal sealed class OwnedRecipeNativeCatalog
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Type _type, _row;
    private readonly FieldInfo _registry;
    private readonly Func<string, Component> _item;
    private readonly Dictionary<string, Component> _built = new(StringComparer.Ordinal);
    private GameObject? _host;
    private bool _busy;
    internal OwnedRecipeNativeCatalog(Func<string, Component> item)
    {
        _item = item; _type = Assembly.Load("Assembly-CSharp").GetType("Behaviour.Crafting.CraftingRecipe", true)!;
        _row = _type.GetNestedType("CraftingRecipeItemRow")!;
        _registry = _type.GetField("allRecipes", BindingFlags.Static | BindingFlags.NonPublic)!;
    }
    private IDictionary Registry => (IDictionary)_registry.GetValue(null)!;
    private static FieldInfo Field(Type type, string name) => type.GetField(name, Flags) ??
        type.GetField("<" + name + ">k__BackingField", Flags) ?? throw new MissingFieldException(type.FullName, name);
    private void Set(object target, string name, object value)
    {
        var field = Field(_type, name); field.SetValue(target, field.FieldType.IsEnum ? Enum.Parse(field.FieldType, (string)value) : value);
    }
    private IList Rows((string Id, int Count)[] rows)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_row))!;
        foreach (var row in rows)
        {
            var native = Activator.CreateInstance(_row)!;
            Field(_row, "item").SetValue(native, _item(row.Id).gameObject);
            Field(_row, "count").SetValue(native, row.Count); list.Add(native);
        }
        return list;
    }
    internal Component Ensure(string id)
    {
        if (_busy) throw new InvalidOperationException("Reentrant recipe construction.");
        var identity = OwnedRecipeIdentity.Read(id);
        if (_built.TryGetValue(id, out var existing)) { Publish(id, existing); return existing; }
        if (_built.Count >= 256 || Registry.Contains(id)) throw new InvalidOperationException("Recipe collision or host limit.");
        _busy = true; GameObject? child = null;
        try
        {
            var inputs = Rows(identity.Inputs); var outputs = Rows(new[] { identity.Output });
            if (_host == null) { _host = new GameObject("VGModAPI.Recipes"); _host.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(_host); }
            child = new GameObject("OwnedRecipe"); child.SetActive(false); child.transform.SetParent(_host.transform, false);
            var recipe = child.AddComponent(_type);
            Set(recipe, "identifier", id); Set(recipe, "customName", identity.Name); Set(recipe, "customCost", identity.Credits);
            Set(recipe, "craftingTime", identity.Seconds); Set(recipe, "craftingRarity", "Standard");
            Set(recipe, "unlockedFromStart", false); Set(recipe, "blueprintAvailable", false); Set(recipe, "craftingEnabled", false);
            Set(recipe, "itemMaterials", inputs); Set(recipe, "results", outputs);
            Set(recipe, "materials", Activator.CreateInstance(Field(_type, "materials").FieldType)!);
            _type.GetMethod("InitializeRecipe")!.Invoke(recipe, new object[] { false });
            // Native customCost=0 means auto-priced; pin the cached dynamic cost for an explicitly free recipe.
            if (identity.Credits == 0) Set(recipe, "dynamicCost", 0);
            Publish(id, recipe); _built.Add(id, recipe); return recipe;
        }
        catch { if (child != null) UnityEngine.Object.Destroy(child); throw; }
        finally { _busy = false; }
    }
    private void Publish(string id, Component recipe)
    {
        if (recipe == null || Registry.Contains(id) && !ReferenceEquals(Registry[id], recipe)) throw new InvalidOperationException("Owned recipe host replaced.");
        Registry[id] = recipe;
    }
    internal void Activate(OwnedRecipeIdentity identity)
    { var recipe = Ensure(identity.NativeId); Set(recipe, "craftingEnabled", true); Set(recipe, "unlockedFromStart", true); }
    internal void Retire(string id)
    { if (_built.TryGetValue(id, out var recipe) && recipe != null) { Set(recipe, "craftingEnabled", false); Set(recipe, "unlockedFromStart", false); } }
    internal void Reinsert() { foreach (var pair in _built) Publish(pair.Key, pair.Value); }
}
