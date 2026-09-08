using System;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class BoardingRulePatches
{
    internal static BoardingRuleAdapter? Adapter;
    internal static class Disable
    {
        internal static bool Prefix(object __instance, object __0, ref bool __result)
        {
            var adapter = Adapter; if (adapter == null) return true;
            var decision = adapter.PrepareDisable(__instance, __0, out var plan);
            if (decision == BoardingDisableDecision.Vanilla) return true;
            __result = false;
            if (decision == BoardingDisableDecision.Allow) { adapter.Convert(plan!); __result = true; }
            return false;
        }
    }
    internal static class Scaling
    {
        internal static Exception? Finalizer(object __instance, Exception? __exception)
        { if (__exception == null) Adapter?.ApplyScaling(__instance); return __exception; }
    }
    internal static class Damage
    {
        internal static void Prefix(object __instance, ref float __0) { if (Adapter != null) __0 = Adapter.Damage(__instance, __0); }
    }
    internal static class CreateShip
    {
        internal static void Prefix(object __0, out object? __state) => __state = Adapter?.BeginCreation(__0, false);
        internal static Exception? Finalizer(object? __state, Exception? __exception) { Adapter?.EndScope(__state); return __exception; }
    }
    internal static class CreateWalk
    {
        internal static void Prefix(object __instance, out object? __state) => __state = Adapter?.BeginCreation(__instance, true);
        internal static Exception? Finalizer(object? __state, Exception? __exception) { Adapter?.EndScope(__state); return __exception; }
    }
    internal static class Estimate
    {
        internal static void Prefix(object __0, out object? __state) => __state = Adapter?.BeginEstimate(__0);
        internal static Exception? Finalizer(object? __state, Exception? __exception) { Adapter?.EndScope(__state); return __exception; }
    }
    internal static class EstimatePower
    {
        internal static void Prefix(ref float __2) { if (Adapter != null) __2 = Adapter.EstimatePower(__2); }
    }
    internal static class Scuttle
    {
        internal static bool Prefix(object __instance, out object? __state)
        {
            __state = Adapter?.BeginCause(__instance, BoardingDamageCause.Scuttle);
            return Adapter?.AllowScuttle(__instance) ?? true;
        }
        internal static Exception? Finalizer(object? __state, Exception? __exception) { Adapter?.EndScope(__state); return __exception; }
    }
    internal static class Explosion
    {
        internal static bool Prefix(object __instance) => Adapter?.AllowExplosion(__instance) ?? true;
    }
    internal static class Cause
    {
        internal static void Prefix(object __instance, MethodBase __originalMethod, out object? __state)
        {
            var cause = __originalMethod.Name switch
            {
                "TickExplosion" => BoardingDamageCause.ReactorExplosion,
                "NotifyHostDestroyed" => BoardingDamageCause.HostDestroyed,
                "TickCompartmentCombat" => BoardingDamageCause.Combat,
                "ApplyAmmoIntegrityDamagePerKill" => BoardingDamageCause.Ammunition,
                "ApplyHazardFacilityDamage" => BoardingDamageCause.Hazard,
                "ThrowGrenade" => BoardingDamageCause.Grenade,
                _ => BoardingDamageCause.Unspecified
            };
            __state = Adapter?.BeginCause(__instance, cause);
        }
        internal static Exception? Finalizer(object? __state, Exception? __exception) { Adapter?.EndScope(__state); return __exception; }
    }
}
