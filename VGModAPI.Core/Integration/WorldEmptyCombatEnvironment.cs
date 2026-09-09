using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>Callback-free enclosing-state restrictions for supported empty sites, not general mission support.</summary>
internal sealed class WorldEmptyCombatEnvironment
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Assembly _assembly;
    private readonly FieldInfo _current, _missions, _storyId, _system, _storyteller;
    private readonly Type[] _excluded;
    internal WorldEmptyCombatEnvironment(Assembly assembly)
    {
        _assembly = assembly;
        FieldInfo Field(string type, string name) => assembly.GetType(type, true)!.GetField(name, Flags) ?? throw new MissingFieldException(type, name);
        _current = Field("Source.Player.GamePlayer", "current"); _missions = Field("Source.Player.GamePlayer", "missions");
        _storyId = Field("Source.MissionSystem.Mission", "storyId"); _system = Field("Source.Galaxy.MapElement", "system");
        _storyteller = Field("Source.Galaxy.SystemMapData", "storyteller");
        _excluded = new[] { "BountyMission", "PatrolMission", "IndustryMission" }.Select(name => assembly.GetType("Source.MissionSystem." + name, true)!).ToArray();
        var player = assembly.GetType("Source.Player.GamePlayer", true)!;
        var names = new[] { "currentBounty", "currentPatrol", "currentIndustry" };
        for (int i = 0; i < names.Length; i++)
            if (player.GetField(names[i], Flags)?.FieldType != _excluded[i]) throw new InvalidDataException("Unexpected mission slot shape.");
    }
    internal void Require(object poi)
    {
        var system = _system.GetValue(poi) ?? throw new InvalidDataException("Empty site lacks its parent system.");
        var storyteller = _storyteller.GetValue(system);
        if (storyteller != null)
        {
            var type = storyteller.GetType();
            var declaring = type.GetMethod("OnGuardUnitsRegenerated", Flags)?.DeclaringType;
            if (type.Assembly != _assembly || declaring?.Assembly != _assembly ||
                (declaring.FullName != "Source.Simulation.World.SystemStoryteller" && declaring.FullName != "Source.Simulation.World.System.FactionSkirmish"))
                throw new InvalidDataException("Uninspected system guard-regeneration callback.");
        }
        var player = _current.GetValue(null) ?? throw new InvalidDataException("Empty profile lacks a current player.");
        var missions = _missions.GetValue(player) as IList;
        if (missions == null || missions.GetType() != _missions.FieldType || missions.Count > 1024) throw new InvalidDataException("Uninspectable mission collection.");
        foreach (var mission in missions)
            if (mission == null || !MissionExcluded(mission, _storyId, _excluded))
                throw new InvalidDataException("Eligible replenishment missions are outside the supported empty Combat profile.");
    }
    internal static bool MissionExcluded(object mission, FieldInfo storyId, Type[] excluded)
        => storyId.GetValue(mission) != null || excluded.Any(type => type.IsInstanceOfType(mission));
}
