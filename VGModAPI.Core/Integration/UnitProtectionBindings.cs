using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class UnitProtectionBindings
{
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        ("Behaviour.Unit.AbstractUnit", "unitData", "Source.Data.AbstractUnitData", false, false),
        ("Behaviour.Weapons.TargetableUnit", "isInvincible", "System.Boolean", false, true),
        ("Behaviour.Weapons.TargetableUnit", "isDestroyed", "System.Boolean", false, false),
        ("Source.Data.AbstractUnitData", "guid", "System.String", false, false),
        ("Source.Data.AbstractUnitData", "currentHullHP", "System.Single", false, true),
        ("Source.Data.AbstractUnitData", "currentArmorHP", "System.Single", false, true),
        ("Source.Data.AbstractUnitData", "currentShieldHP", "System.Single", false, true),
        ("Source.Data.AbstractUnitData", "empCharge", "System.Single", false, true),
        ("Source.Data.AbstractUnitData", "battleDamage", "System.Collections.Generic.List`1<Source.Mining.SpriteBreakPoint>", false, true)
    };
    /// <summary>SpaceShip.TakeDamage delegates to this base method, so one bracket covers all units.</summary>
    internal static readonly MethodBinding[] Methods =
    {
        new("protectDamage", "Behaviour.Unit.AbstractUnit", "TakeDamage", false, "System.Void", "Behaviour.Weapons.DamageData")
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Methods);
    }
}
