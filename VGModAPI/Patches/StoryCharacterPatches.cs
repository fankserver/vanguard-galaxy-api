using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

/// <summary>Owned lookup names never exist in the vanilla registry; every other lookup runs vanilla.</summary>
internal static class StoryCharacterPatches
{
    internal static StoryCharacterRuntime? Runtime;
    internal static class Lookup
    {
        internal static bool Prefix(string name, ref object? __result)
        {
            var owned = Runtime?.ResolveOwned(name);
            if (owned == null) return true;
            __result = owned;
            return false; // Skips only the null-returning native miss and its warning.
        }
        internal static void Postfix(string name, ref object? __result) => Runtime?.ApplyExtensions(name, __result);
    }
}
