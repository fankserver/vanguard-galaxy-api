using System;
using System.Collections.Generic;
using System.Reflection;
using System.Collections;
using UnityEngine;
using VGModAPI.Core;

namespace VGModAPI;

/// <summary>Plain trade-goods hosts only. Catalog clears reinsert the same hosts, preserving live inventory references.</summary>
internal sealed class OwnedItemNativeCatalog : IDisposable
{
    private readonly Dictionary<string, Component> _built = new(StringComparer.Ordinal);
    private readonly Type _itemType;
    private readonly FieldInfo _catalog;
    private readonly PropertyInfo _icon;
    private readonly MethodInfo _initialize;
    private readonly Dictionary<string, FieldInfo> _fields = new(StringComparer.Ordinal);
    private GameObject? _host;
    private bool _busy, _disposed;
    internal OwnedItemNativeCatalog()
    {
        _itemType = Assembly.Load("Assembly-CSharp").GetType("Behaviour.Item.InventoryItemType", true)!;
        _catalog = _itemType.GetField("allItems", BindingFlags.Static | BindingFlags.NonPublic)!;
        _icon = _itemType.GetProperty("icon")!; _initialize = _itemType.GetMethod("InitializeItem")!;
        foreach (var name in new[] { "identifier", "itemCategory", "storageOverride", "displayName", "description", "icon", "m3", "baseCost", "rarity", "canJettison", "canSell" })
            _fields.Add(name, _itemType.GetField("<" + name + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("InventoryItemType." + name));
    }
    internal bool Loaded => Catalog.Count != 0;
    private IDictionary Catalog => (IDictionary)_catalog.GetValue(null)!;
    private void Set(Component item, string field, object value) => _fields[field].SetValue(item, _fields[field].FieldType.IsEnum ? Enum.Parse(_fields[field].FieldType, (string)value) : value);
    internal Component Ensure(string id)
    {
        if (_disposed || _busy) throw new InvalidOperationException("Owned item construction unavailable or reentrant.");
        var identity = OwnedItemIdentity.Read(id);
        if (_built.TryGetValue(id, out var existing))
        {
            if (existing == null) throw new InvalidOperationException("Owned item host was destroyed while retained.");
            Publish(id, existing); return existing;
        }
        if (_built.Count >= 512) throw new InvalidOperationException("Owned item catalog limit exceeded.");
        var d = identity.Definition;
        var iconSource = Catalog[d.IconItemId] as Component;
        if (iconSource == null || _icon.GetValue(iconSource) is not Sprite icon || icon == null)
            throw new InvalidOperationException("Vanilla item icon dependency unavailable.");
        if (Catalog.Contains(id)) throw new InvalidOperationException("Owned item identity collides with another catalog entry.");
        _busy = true; GameObject? child = null;
        try
        {
            if (_host == null)
            {
                _host = new GameObject("VGModAPI.PlainItems"); _host.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_host);
            }
            child = new GameObject("OwnedItem"); child.SetActive(false); child.transform.SetParent(_host.transform, false);
            var item = child.AddComponent(_itemType);
            Set(item, "identifier", id); Set(item, "itemCategory", "TradeGoods");
            Set(item, "storageOverride", d.Storage == OwnedItemStorage.Armory ? "Armory" : "Materials");
            Set(item, "displayName", d.Name); Set(item, "description", d.Description); Set(item, "icon", icon);
            Set(item, "m3", d.Volume); Set(item, "baseCost", d.BaseCost); Set(item, "rarity", "Standard");
            Set(item, "canJettison", true); Set(item, "canSell", true);
            _initialize.Invoke(item, new object?[] { null });
            Publish(id, item); _built.Add(id, item); return item;
        }
        catch { if (child != null) UnityEngine.Object.Destroy(child); throw; }
        finally { _busy = false; }
    }
    internal Component ResolvePlain(string id)
    {
        var item = OwnedItemIdentity.IsReserved(id) ? Ensure(id) : Catalog[id] as Component;
        if (item == null) throw new InvalidOperationException("Recipe item dependency unavailable.");
        string category = _fields["itemCategory"].GetValue(item)!.ToString()!;
        if (category != "TradeGoods" && category != "RefinedProduct" && category != "Ore" && category != "Salvage" && category != "Junk" && category != "Crystal")
            throw new InvalidOperationException("Recipe item shape is not supported plain goods.");
        return item;
    }
    private void Publish(string id, Component item)
    {
        if (Catalog.Contains(id) && !ReferenceEquals(Catalog[id], item))
            throw new InvalidOperationException("Owned item catalog entry replaced by another author.");
        Catalog[id] = item;
    }
    internal void Reinsert()
    {
        if (_disposed || _busy) throw new InvalidOperationException("Owned item catalog unavailable.");
        foreach (var pair in _built) Publish(pair.Key, pair.Value);
    }
    // Only dispose when no live inventory can retain one of these objects.
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var pair in _built)
            if (ReferenceEquals(Catalog[pair.Key], pair.Value)) Catalog.Remove(pair.Key);
        _built.Clear(); if (_host != null) UnityEngine.Object.Destroy(_host); _host = null;
    }
}
