using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VGModAPI;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>
/// The reflection the native quarantine guards need, and nothing else: the story identifier of a
/// mission, and which objective objects belong to it. It is deliberately independent of
/// <see cref="StoryNativeBindings"/>, because the guards must work when the story module itself is
/// disabled, unbound or absent — that is exactly when an orphaned owned mission is dangerous.
///
/// It also verifies that every objective kind this API can install uses the BASE trigger method, so
/// the one guard on that method really does cover everything the API can put into a save. A kind that
/// overrode it would need its own guard, so binding refuses instead of quarantining incompletely.
/// </summary>
internal sealed class StoryProtectionGuard
{
    private readonly FieldInfo _storyId, _nextOnFailed;
    private readonly PropertyInfo _steps, _objectives;
    private readonly Type _mission;

    internal StoryProtectionGuard(Assembly assembly)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));
        _mission = assembly.GetType(BindingCatalog.Mission, true)!;
        var step = assembly.GetType("Source.MissionSystem.MissionStep", true)!;
        var objective = assembly.GetType("Source.MissionSystem.MissionObjective", true)!;
        _storyId = _mission.GetField("storyId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? throw new MissingFieldException(_mission.FullName, "storyId");
        if (_storyId.FieldType != typeof(string)) throw new MissingFieldException(_mission.FullName, "storyId");
        // The game's abandon/retry re-adds `nextMissionOnFailed ?? storyId`, so the guard has to see it.
        _nextOnFailed = _mission.GetField("nextMissionOnFailed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? throw new MissingFieldException(_mission.FullName, "nextMissionOnFailed");
        if (_nextOnFailed.FieldType != typeof(string)) throw new MissingFieldException(_mission.FullName, "nextMissionOnFailed");
        _steps = Property(_mission, "steps");
        _objectives = Property(step, "objectives");
        var trigger = objective.GetMethod("ProcessMissionTrigger",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(objective.FullName, "ProcessMissionTrigger");
        if (!trigger.IsVirtual) throw new MissingMethodException(objective.FullName, "ProcessMissionTrigger");
        foreach (StoryObjectiveKind kind in Enum.GetValues(typeof(StoryObjectiveKind)))
        {
            if (StoryContentPolicy.RefuseObjective(kind) != null) continue;      // not installable anyway
            var type = assembly.GetType(StoryContentPolicy.ObjectiveNamespace + "." + StoryContentPolicy.ObjectiveTypeName(kind), true)!;
            // The scripted override has its own mandatory catalog binding and identical guard prefix.
            if (kind == StoryObjectiveKind.Scripted) continue;
            if (type.GetMethod("ProcessMissionTrigger",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null)
                throw new NotSupportedException("Objective '" + type.FullName
                    + "' overrides ProcessMissionTrigger, so the base guard would not cover it.");
        }
    }

    private static PropertyInfo Property(Type type, string name) => type.GetProperty(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        ?? throw new MissingMemberException(type.FullName, name);

    internal bool IsScriptedObjective(object value)
        => value.GetType().FullName == "Source.MissionSystem.Objectives.TriggerObjective";

    internal bool IsMission(object? value) => value != null && _mission.IsInstanceOfType(value);

    /// <summary>The story identifier of a mission, or null. Reading a field mutates nothing.</summary>
    internal string? StoryId(object mission) => _mission.IsInstanceOfType(mission) ? (string?)_storyId.GetValue(mission) : null;

    /// <summary>The follow-up identifier the game would install instead of this mission's own.</summary>
    internal string? NextMissionOnFailed(object mission)
        => _mission.IsInstanceOfType(mission) ? (string?)_nextOnFailed.GetValue(mission) : null;

    /// <summary>Every objective object this mission holds, so a guard can recognise one by identity.</summary>
    internal IEnumerable<object> Objectives(object mission)
    {
        if (!_mission.IsInstanceOfType(mission)) yield break;
        if (_steps.GetValue(mission) is not IEnumerable steps) yield break;
        foreach (var step in steps)
        {
            if (step == null || _objectives.GetValue(step) is not IEnumerable objectives) continue;
            foreach (var objective in objectives) if (objective != null) yield return objective;
        }
    }
}
