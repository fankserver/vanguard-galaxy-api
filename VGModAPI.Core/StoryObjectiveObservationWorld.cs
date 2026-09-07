using System;

namespace VGModAPI.Core;

/// <summary>Read-only progress from the current vanilla-held occurrence, never a retained native reference.</summary>
internal interface IStoryObjectiveObservationWorld
{
    int? ReadProgress(string identifier, StoryObjectiveLayout.Slot slot, StoryObjective expected, Func<bool> stillValid);
}
