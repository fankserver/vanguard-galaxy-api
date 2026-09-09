using System;
using VGModAPI.Core;

namespace VGModAPI.Patches;
internal static class OwnedItemPatches
{
    internal static Action<string>? Resolve;
    internal static Action? Rebuild;
    internal static Action<object>? ValidateValue;
    public static void FromJson(object[] __args)
    {
        foreach (var value in __args)
            if (value != null && value.GetType().FullName == "LightJson.JsonValue") ValidateValue?.Invoke(value);
    }
    public static void Lookup(object[] __args)
    {
        if (__args.Length == 0 || __args[0] is not string id || !OwnedItemIdentity.IsReserved(id)) return;
        if (Resolve == null) throw new InvalidOperationException("Owned item support is stopped.");
        Resolve(id);
    }
    public static void Loaded() => Rebuild?.Invoke();
}
