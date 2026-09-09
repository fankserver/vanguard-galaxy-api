using System;
using VGModAPI.Core;

namespace VGModAPI.Patches;
internal static class OwnedRecipePatches
{
    internal static Action<string>? Resolve;
    internal static Action? Reload;
    public static void Lookup(object[] __args)
    {
        if (__args.Length == 0 || __args[0] is not string id || !OwnedRecipeIdentity.IsReserved(id)) return;
        if (Resolve == null) throw new InvalidOperationException("Owned recipe support stopped.");
        Resolve(id);
    }
    public static void Loaded() => Reload?.Invoke();
}
