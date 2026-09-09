using System;
using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

internal static class WorldLoadPatches
{
    internal static IWorldLoadHookHost? Host = null;

    internal static class Recall
    {
        internal static bool Prefix(object __instance, ref object? __result)
        {
            var host = Host;
            if (host == null || !host.TryRecall(__instance, out var result)) return true;
            __result = result ?? throw new InvalidOperationException("Missing verified world load input.");
            return false;
        }
    }

    internal static class Factory
    {
        internal sealed class Capture
        {
            internal readonly IWorldFactoryCaptureHost Host;
            internal readonly object Token;
            internal Capture(IWorldFactoryCaptureHost host, object token) { Host = host; Token = token; }
        }
        internal static void Prefix(object val, out Capture? __state)
        {
            __state = null;
            var host = Host;
            if (host is IWorldFactoryCaptureHost captureHost)
            {
                var token = captureHost.BeginFactory(val);
                if (token != null) __state = new Capture(captureHost, token);
            }
            else host?.RequireFactory(val);
        }
        internal static bool ConstructPrefix(object val, ref object __result, out Capture? __state)
        {
            Prefix(val, out __state);
            if (__state?.Host is not IWorldOwnedFactoryHost owned) return true;
            __result = owned.ConstructFactory(__state.Token); return false;
        }
        internal static Exception? Finalizer(Capture? __state, object __result, Exception? __exception)
        {
            if (__exception == null && __state != null) __state.Host.CompleteFactory(__state.Token, __result);
            return __exception;
        }
    }
}
