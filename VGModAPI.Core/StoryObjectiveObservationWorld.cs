using System;

namespace VGModAPI.Core;

/// <summary>
/// One verified observation of a live objective. A reading either carries progress, or reports that
/// the objective's authored destination no longer exists - which is world state, not a refusal.
/// </summary>
internal readonly struct StoryObjectiveReading
{
    internal int? Progress { get; }
    internal bool DestinationLost { get; }
    private StoryObjectiveReading(int? progress, bool destinationLost) { Progress = progress; DestinationLost = destinationLost; }
    internal static StoryObjectiveReading Of(int progress) => new(progress, false);
    /// <summary>The session is real and the objective is ours; only its destination is gone.</summary>
    internal static StoryObjectiveReading Lost => new(null, true);
}

/// <summary>Read-only progress from the current vanilla-held occurrence, never a retained native reference.</summary>
internal interface IStoryObjectiveObservationWorld
{
    StoryObjectiveReading? ReadProgress(string identifier, StoryObjectiveLayout.Slot slot, StoryObjective expected, Func<bool> stillValid);
}
