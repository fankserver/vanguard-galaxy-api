using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class MissionBindings
{
    private readonly FieldInfo _player, _missions, _archive, _name, _definition, _failed;
    private readonly FieldInfo[] _special;
    private readonly PropertyInfo _steps, _objectives;
    private readonly FieldInfo _category;
    private readonly Type _mission, _mining;
    internal MissionBindings(Assembly assembly)
    {
        var player = assembly.GetType(BindingCatalog.Player, true)!;
        _mission = assembly.GetType(BindingCatalog.Mission, true)!;
        _player = Field(player, "current"); _missions = Field(player, "missions"); _archive = Field(player, "missionsArchive");
        _special = new[] { "currentBounty", "currentPatrol", "currentIndustry" }.Select(name => Field(player, name)).ToArray();
        _name = Field(_mission, "name"); _definition = Field(_mission, "storyId"); _failed = Field(_mission, "failed");
        _steps = Property(_mission, "steps");
        _objectives = Property(assembly.GetType("Source.MissionSystem.MissionStep", true)!, "objectives");
        _mining = assembly.GetType("Source.MissionSystem.Objectives.Mining", true)!;
        _category = Field(_mining, "itemCategory");
    }
    private static FieldInfo Field(Type type, string name) => type.GetField(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly) ?? throw new MissingFieldException(type.FullName, name);
    private static PropertyInfo Property(Type type, string name) => type.GetProperty(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) ?? throw new MissingMemberException(type.FullName, name);
    internal object? Player => _player.GetValue(null);
    internal bool IsMission(object value) => _mission.IsInstanceOfType(value);
    internal object[] Active(object player)
    {
        var result = ((IEnumerable)_missions.GetValue(player)!).Cast<object>().ToList();
        foreach (var field in _special)
        {
            var value = field.GetValue(player);
            if (value != null && !result.Any(existing => ReferenceEquals(existing, value))) result.Add(value);
        }
        return result.ToArray();
    }
    internal bool Contains(object player, object mission) => Active(player).Any(value => ReferenceEquals(value, mission));
    internal bool Failed(object mission) => (bool)_failed.GetValue(mission)!;
    internal string? Definition(object mission) => (string?)_definition.GetValue(mission);
    internal string Name(object mission) => (string?)_name.GetValue(mission) ?? "";
    internal int ArchiveCount(object player, string? definition) => definition == null ? 0 :
        ((IEnumerable)_archive.GetValue(player)!).Cast<object>().Count(value => string.Equals(value as string, definition, StringComparison.Ordinal));
    internal string[] Tags(object mission)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in (IEnumerable)_steps.GetValue(mission)!)
            foreach (var objective in (IEnumerable)_objectives.GetValue(step)!)
            {
                tags.Add("type:" + objective.GetType().FullName);
                if (_mining.IsInstanceOfType(objective) && _category.GetValue(objective) is { } category)
                    tags.Add("item-category:" + category);
            }
        return tags.OrderBy(tag => tag, StringComparer.Ordinal).ToArray();
    }
}
