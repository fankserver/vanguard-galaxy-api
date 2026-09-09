using System;
using System.Collections;
using System.IO;

namespace VGModAPI.Core.Integration;

internal sealed partial class WorldJsonInspection
{
    // Item definitions are retained in vanilla identifiers; quantities and instance flags remain in vanilla inventories.
    // Scan keys as well as values because some native stores serialize identifiers as dictionary keys.
    internal bool HasOwnedItems(object root, Action<string>? prepare = null)
    {
        int visited = 0; bool found = false;
        void Text(string text)
        {
            if (!OwnedItemIdentity.IsReserved(text)) return;
            _ = OwnedItemIdentity.Read(text); prepare?.Invoke(text); found = true;
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
                    Text((string)type.GetProperty("Key")!.GetValue(entry)!);
                    value = type.GetProperty("Value")!.GetValue(entry)!;
                }
                if ((bool)_isString.GetValue(value)!) Text((string)_string.GetValue(value)!);
                else if ((bool)_isObject.GetValue(value)!) Container((IEnumerable)_object.GetValue(value)!, depth + 1, true);
                else if ((bool)_isArray.GetValue(value)!) Container((IEnumerable)_array.GetValue(value)!, depth + 1, false);
            }
        }
        Container((IEnumerable)root, 0, true);
        return found;
    }
}
