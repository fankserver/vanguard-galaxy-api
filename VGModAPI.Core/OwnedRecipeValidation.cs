using System;
using System.Collections.Generic;

namespace VGModAPI.Core;
internal static class OwnedRecipeValidation
{
    internal static void Validate(string owner, OwnedRecipeDefinition definition)
    {
        if (definition == null || definition.Ingredients.Count > 8) throw new ArgumentException("Missing or oversized recipe.");
        var inputs = new (string, int)[definition.Ingredients.Count]; var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < inputs.Length; i++)
        {
            var row = definition.Ingredients[i];
            if (row == null || !seen.Add(Key(row.Item))) throw new ArgumentException("Duplicate ingredient.");
            inputs[i] = ("validation", row.Count);
        }
        _ = Key(definition.Result.Item);
        _ = new OwnedRecipeIdentity(owner, definition.LocalId, definition.Revision, definition.Name, definition.Credits,
            definition.Seconds, inputs, ("validation", definition.Result.Count));
    }
    private static string Key(RecipeItemReference reference)
    {
        if (reference.Owned is { } owned)
        {
            _ = new OwnedRecipeIdentity(owned.ProviderId, owned.LocalId, 1, "validation", 0, 1, Array.Empty<(string, int)>(), ("validation", 1));
            return "owned:" + owned.ProviderId + ":" + owned.LocalId;
        }
        var id = reference.VanillaId;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 500 || id.StartsWith("vgmodapi.", StringComparison.Ordinal)) throw new ArgumentException("Invalid vanilla item reference.");
        return "vanilla:" + id;
    }
}
