using System;
using System.Collections;
using System.IO;

namespace VGModAPI.Core.Integration;

internal sealed partial class WorldJsonInspection
{
    // Item definitions are retained in vanilla identifiers; quantities and instance flags remain in vanilla inventories.
    // Scan keys as well as values because some native stores serialize identifiers as dictionary keys.
    internal bool HasOwnedItems(object root, Action<string>? prepare = null, Action<string>? prepareRecipe = null)
    {
        int visited = 0; bool found = false;
        var identities = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        var recipes = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        void Text(string text)
        {
            if (OwnedRecipeIdentity.IsReserved(text)) { _ = OwnedRecipeIdentity.Read(text); recipes.Add(text); found = true; return; }
            if (!OwnedItemIdentity.IsReserved(text)) return;
            _ = OwnedItemIdentity.Read(text); identities.Add(text); found = true;
        }
        void Container(IEnumerable entries, int depth, bool dictionary)
        {
            if (depth > 64) throw new InvalidDataException("Item reference nesting exceeds limit.");
            foreach (var entry in entries)
            {
                if (++visited > 1000000) throw new InvalidDataException("Item reference scan exceeds limit.");
                object value = entry;
                if (dictionary)
                {
                    var type = entry.GetType();
                    var key = (string)type.GetProperty("Key")!.GetValue(entry)!;
                    Text(key);
                    value = type.GetProperty("Value")!.GetValue(entry)!;
                    if ((key == "itemTypeId" || key == "itemType") && (bool)_isString.GetValue(value)! &&
                        (OwnedItemIdentity.IsReserved((string)_string.GetValue(value)!) || OwnedRecipeIdentity.IsReserved((string)_string.GetValue(value)!)))
                        throw new InvalidDataException("Owned goods do not support leveled, lore or builder object representations.");
                }
                if ((bool)_isString.GetValue(value)!) Text((string)_string.GetValue(value)!);
                else if ((bool)_isObject.GetValue(value)!) Container((IEnumerable)_object.GetValue(value)!, depth + 1, true);
                else if ((bool)_isArray.GetValue(value)!) Container((IEnumerable)_array.GetValue(value)!, depth + 1, false);
            }
        }
        Container((IEnumerable)root, 0, true);
        foreach (var id in identities) prepare?.Invoke(id);
        foreach (var id in recipes) prepareRecipe?.Invoke(id);
        return found;
    }
    internal void RequirePlainItemValue(object value)
    {
        if ((bool)_isString.GetValue(value)! && OwnedRecipeIdentity.IsReserved((string)_string.GetValue(value)!))
            throw new InvalidDataException("Recipe identity is not an item.");
        if ((bool)_isObject.GetValue(value)! && HasOwnedItems(_object.GetValue(value)!))
            throw new InvalidDataException("Owned goods require a plain string item representation.");
        if ((bool)_isString.GetValue(value)! && OwnedItemIdentity.IsReserved((string)_string.GetValue(value)!))
            _ = OwnedItemIdentity.Read((string)_string.GetValue(value)!);
    }
}
