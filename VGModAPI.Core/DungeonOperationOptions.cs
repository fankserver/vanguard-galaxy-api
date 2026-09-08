using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Resolved native creation options, retained even before a simulation exists.</summary>
internal sealed class DungeonOperationOptions
{
    internal IReadOnlyDictionary<string, int> AssignedCrew { get; }
    internal string Ammo { get; }
    internal string Stealth { get; }
    internal bool AutoAcceptBuyOut { get; }
    internal bool AutoMove { get; }
    internal int? PriorityCompartment { get; }
    internal DungeonOperationOptions(IReadOnlyDictionary<string, int> crew, string ammo, string stealth, bool autoAcceptBuyOut, bool autoMove, int? priorityCompartment)
    {
        if (crew == null || crew.Count > 64 || crew.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Value < 0 || pair.Value > 10000) || crew.Values.Sum(value => (long)value) > 10000)
            throw new ArgumentException("Invalid saved assigned crew.");
        if (string.IsNullOrWhiteSpace(ammo) || ammo.Length > 64 || string.IsNullOrWhiteSpace(stealth) || stealth.Length > 64 || priorityCompartment < 0)
            throw new ArgumentException("Invalid saved operation options.");
        AssignedCrew = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(crew, StringComparer.Ordinal));
        Ammo = ammo; Stealth = stealth; AutoAcceptBuyOut = autoAcceptBuyOut; AutoMove = autoMove; PriorityCompartment = priorityCompartment;
    }
}
