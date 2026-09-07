using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class StoryObjectiveLayout
{
    internal readonly struct Slot
    {
        internal string Key { get; }
        internal int Step { get; }
        internal int Objective { get; }
        internal StoryObjectiveKind Kind { get; }
        internal int Required { get; }
        internal int Progress { get; }
        internal Slot(string key, int step, int objective, StoryObjectiveKind kind, int required = 1, int progress = 0)
        { Key = key; Step = step; Objective = objective; Kind = kind; Required = required; Progress = progress; }
    }

    internal int Revision { get; }
    internal const int MaxSlots = StoryMissionDefinition.MaxSteps * StoryStep.MaxObjectives;
    private readonly Dictionary<string, Slot> _slots;
    internal IReadOnlyList<Slot> Slots { get; }

    internal StoryObjectiveLayout(StoryMissionDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        Revision = definition.ContentRevision;
        _slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        for (int step = 0; step < definition.Steps.Count; step++)
            for (int objective = 0; objective < definition.Steps[step].Objectives.Count; objective++)
            {
                var item = definition.Steps[step].Objectives[objective];
                if (item.LocalKey == null) continue;
                _slots.Add(item.LocalKey, new Slot(item.LocalKey, step, objective, item.Kind, Math.Max(1, item.RequiredAmount)));
            }
        Slots = Array.AsReadOnly(_slots.Values.OrderBy(slot => slot.Key, StringComparer.Ordinal).ToArray());
    }

    internal StoryObjectiveLayout(IEnumerable<Slot> slots, int revision = 1)
    {
        if (slots == null) throw new ArgumentNullException(nameof(slots));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
        var copy = slots.Take(MaxSlots + 1).ToArray();
        if (copy.Length > MaxSlots) throw new ArgumentException("Objective layout exceeds its bound.", nameof(slots));
        _slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        var positions = new HashSet<(int, int)>();
        foreach (var slot in copy)
        {
            if (!StoryContentId.IsValidSegment(slot.Key) || slot.Step < 0 || slot.Step >= StoryMissionDefinition.MaxSteps
                || slot.Objective < 0 || slot.Objective >= StoryStep.MaxObjectives || !Enum.IsDefined(typeof(StoryObjectiveKind), slot.Kind)
                || slot.Required is < 1 or > StoryObjective.MaxAmount || slot.Progress < 0 || slot.Progress > slot.Required
                || _slots.ContainsKey(slot.Key) || !positions.Add((slot.Step, slot.Objective)))
                throw new ArgumentException("Invalid or duplicate objective layout slot.", nameof(slots));
            _slots.Add(slot.Key, slot);
        }
        Slots = Array.AsReadOnly(_slots.Values.OrderBy(slot => slot.Key, StringComparer.Ordinal).ToArray());
    }

    internal bool TryResolve(string key, out Slot slot) => _slots.TryGetValue(key, out slot);

    internal StoryObjectiveLayout WithProgress(string key, int progress)
    {
        if (!TryResolve(key, out var current)) throw new ArgumentException("Unknown objective key.", nameof(key));
        if (progress < current.Progress || progress > current.Required) throw new ArgumentOutOfRangeException(nameof(progress));
        return new StoryObjectiveLayout(Slots.Select(slot => slot.Key == key
            ? new Slot(slot.Key, slot.Step, slot.Objective, slot.Kind, slot.Required, progress) : slot), Revision);
    }

    internal bool TryMigrate(StoryMissionDefinition definition, out StoryObjectiveLayout migrated)
    {
        migrated = this;
        if (definition.MigratesFromRevision != Revision || definition.ContentRevision <= Revision || Slots.Count == 0
            || Slots.Any(slot => slot.Kind != StoryObjectiveKind.Scripted)) return false;
        var destination = new StoryObjectiveLayout(definition);
        if (destination.Slots.Any(slot => slot.Kind != StoryObjectiveKind.Scripted) || !TryMapTo(destination, out _)) return false;
        migrated = new StoryObjectiveLayout(destination.Slots.Select(slot => TryResolve(slot.Key, out var source)
            ? new Slot(slot.Key, slot.Step, slot.Objective, slot.Kind, slot.Required, source.Progress) : slot), destination.Revision);
        return true;
    }

    internal bool SamePositions(StoryObjectiveLayout other)
        => Revision == other.Revision && Slots.Count == other.Slots.Count && Slots.All(source => other.TryResolve(source.Key, out var target)
            && source.Step == target.Step && source.Objective == target.Objective && source.Kind == target.Kind && source.Required == target.Required);

    // A missing key or changed objective kind requires an explicit migration, never positional
    // guessing. Labels and localization are deliberately absent from identity comparisons.
    internal bool TryMapTo(StoryObjectiveLayout destination, out IReadOnlyList<(Slot Source, Slot Destination)> mapping)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        var result = new List<(Slot, Slot)>();
        foreach (var source in Slots)
        {
            if (!destination.TryResolve(source.Key, out var target) || source.Kind != target.Kind || source.Required != target.Required)
            {
                mapping = Array.Empty<(Slot, Slot)>();
                return false;
            }
            result.Add((source, target));
        }
        mapping = result.AsReadOnly();
        return true;
    }
}
