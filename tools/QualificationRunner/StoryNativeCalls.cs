using System;
using System.Reflection;

namespace VGModAPI.Qualification;

internal static class StoryNativeCalls
{
    internal static MethodInfo CompleteMission(Type player, Type mission)
        => player.GetMethod("CompleteMission", new[] { mission, typeof(bool) })
            ?? throw new MissingMethodException(player.FullName, "CompleteMission(Mission,bool)");
}
