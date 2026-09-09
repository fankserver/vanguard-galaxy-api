using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace VGModAPI.Core.Integration;

internal sealed partial class WorldJsonInspection
{
    private static readonly HashSet<string> EmptyCombatFields = new(StringComparer.Ordinal)
    {
        "guid", "position", "name", "level", "systemName", "faction", "type",
        "dangerLevel", "hazardsDescription", "lastVisitedTime", "backgroundSeed", "contentSeed",
        "lastVisitedX", "timeLeft", "hidden", "asteroidsInitialized", "hasAsteroids"
    };
    // Used in addition to ordinary identity/schema checks, never as a substitute for them.
    internal void RequireEmptyCombatJson(object poi)
    {
        if (poi.GetType() != _objectType) throw new InvalidDataException("Expected exact native world JSON object.");
        foreach (var entry in (IEnumerable)poi)
        {
            var key = (string)entry.GetType().GetProperty("Key")!.GetValue(entry)!;
            if (!EmptyCombatFields.Contains(key)) throw new InvalidDataException("World field is outside the empty qualification profile: " + key);
        }
        foreach (string key in new[] { "hidden", "asteroidsInitialized", "hasAsteroids" })
        {
            if ((bool)_isNull.GetValue(Field(poi, key))!) continue;
            bool value = Boolean(poi, key);
            if ((key == "hasAsteroids" || key == "asteroidsInitialized") && value) throw new InvalidDataException("Asteroids are outside the empty qualification profile.");
        }
    }
}
