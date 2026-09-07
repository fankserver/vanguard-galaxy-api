namespace VGModAPI.Core;

/// <summary>Optional live objective mutation surface; implementations resolve the current player on every call.</summary>
internal interface IStoryObjectiveWorld
{
    StoryWorldResult MigrateScripted(string identifier, StoryMissionDefinition definition, StoryObjectiveLayout source,
        StoryObjectiveLayout destination, System.Func<bool> stillValid);
    StoryWorldResult SetScriptedProgress(string identifier, StoryObjectiveLayout.Slot slot, int progress, System.Func<bool>? stillValid = null);
}
