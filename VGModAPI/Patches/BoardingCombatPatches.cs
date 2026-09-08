using System;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class BoardingCombatPatches
{
    internal static BoardingCombatAdapter? Adapter = null;
    internal static class Scope
    {
        internal static void Prefix(object __instance, out object? __state) => __state = Adapter?.Begin(__instance);
        internal static Exception? Finalizer(object? __state, Exception? __exception) { Adapter?.End(__state); return __exception; }
    }
    internal static class PlayerReinforcements
    {
        internal static bool Prefix(object __instance) => Adapter?.AllowPanelReinforcement(__instance) ?? true;
    }
    internal static class Power
    {
        internal static void Postfix(object __instance, ref float __result)
        { if (Adapter != null) __result = Adapter.UnitValue(__instance, BoardingCombatPolicyKind.Power, __result); }
    }
    internal static class Health
    {
        internal static void Prefix(object __instance, ref float __0)
        { if (Adapter != null) __0 = Adapter.UnitValue(__instance, BoardingCombatPolicyKind.InitialHealth, __0); }
    }
    internal static class Casualties
    {
        internal static void Prefix(object __instance, int __1, ref float __2, bool __3)
        { if (Adapter != null) __2 = Adapter.Casualties(__instance, __1, __2, __3); }
    }
    internal static class AttackerState
    {
        internal static void Prefix(object __instance, object __0) => Adapter?.ApplyPendingMorale(__instance, __0);
    }
    internal static class Morale
    {
        internal static void Prefix(object __instance, out object? __state) => __state = Adapter?.BeginMorale(__instance);
        internal static Exception? Finalizer(object? __state, Exception? __exception) { Adapter?.EndMorale(__state, __exception == null); return __exception; }
    }
    private static bool Allow(object simulation, string method, object[] args)
    {
        var kind = method switch
        {
            "TrySurrenderInCombat" or "CheckMassSurrender" or "CheckAttackerMoraleCollapse" => BoardingCombatPolicyKind.Surrender,
            "TryDefectInCombat" or "TrySideSwitch" => BoardingCombatPolicyKind.Defection,
            "TickReinforcementSchedule" => BoardingCombatPolicyKind.Reinforcement,
            "FireHazardEvent" => BoardingCombatPolicyKind.Hazard,
            "TryAirlockVent" or "TriggerRandomVentDamage" => BoardingCombatPolicyKind.Venting,
            _ => throw new ArgumentException("Unmapped combat effect boundary.", nameof(method))
        };
        var side = method is "CheckAttackerMoraleCollapse" or "FireHazardEvent" or "TryAirlockVent" or "TriggerRandomVentDamage" ? BoardingCombatSide.Attackers : BoardingCombatSide.Defenders;
        var unit = method is "TrySurrenderInCombat" or "TryDefectInCombat" ? args[0] : null;
        int? room = unit != null ? (int)args[1] : null;
        return Adapter?.Allow(simulation, kind, side, unit, room) ?? true;
    }
    internal static class BoolEffect
    {
        internal static bool Prefix(object __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
        {
            if (Allow(__instance, __originalMethod.Name, __args)) return true;
            __result = false; return false;
        }
    }
    internal static class VoidEffect
    {
        internal static bool Prefix(object __instance, MethodBase __originalMethod, object[] __args) => Allow(__instance, __originalMethod.Name, __args);
    }
}
