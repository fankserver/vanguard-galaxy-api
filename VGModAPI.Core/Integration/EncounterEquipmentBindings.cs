using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>
/// Single source of truth for the native members the authored-encounter equipment path touches, in the
/// same catalog shape as the other binding catalogs. Both the runtime (reflection resolution) and the
/// InstalledGame Cecil tests consume this, so a game reshape that breaks one is caught by
/// <c>make check-bindings</c>. Declared arity is the FULL method arity — trailing optional parameters
/// are listed because MethodInfo.Invoke never fills them — and canonical/catalog spelling matches
/// <see cref="NativeTypeName"/> (same as the installed-metadata tests).
/// </summary>
internal static class EncounterEquipmentBindings
{
    internal const string Player = "Source.Player.GamePlayer";
    internal const string GameMath = "Source.Util.GameMath";
    internal const string ShipData = "Source.SpaceShip.SpaceShipData";

    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (Player, "level", "System.Int32", false, false),                       // property
        (GameMath, "maxLevel", "System.Int32", true, false),                   // static property
        (ShipData, "shipRarity", "Source.Item.Rarity", false, true)            // field
    };

    internal static readonly MethodBinding[] Methods =
    {
        new("setStatBoost", "Source.Data.AbstractUnitData", "SetStatBoost", false, "System.Void",
            "System.String", "System.String", "Source.Item.EquipStat", "System.Single", "System.Single",
            "System.Int32", "System.Int32", "System.Nullable`1<System.Single>", "System.Boolean"),
        new("loadDefaultEquipment", ShipData, "LoadDefaultEquipment", false, "System.Void",
            "System.Int32", "System.Single", "System.String",
            "System.Nullable`1<Source.Util.GameplayType>", "System.Nullable`1<Source.Item.Rarity>",
            "System.Func`2<Source.Item.EquipmentSlot,Source.Item.Rarity>", "System.Boolean",
            "System.Nullable`1<Behaviour.Weapons.TargetLayer>"),
        new("applyItemLevelCap", GameMath, "ApplyItemLevelCap", true, "System.Int32", "System.Int32", "System.Boolean"),
        new("damageMultiplier", GameMath, "DamageMultiplier", true, "System.Single", "System.Single")
    };

    /// <summary>
    /// Verifies every declared member shape and resolves every declared method against the installed
    /// assembly. Throws on any mismatch; the caller isolates failures so a reshape only degrades the
    /// optional equipment feature, never world protection.
    /// </summary>
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Methods);
    }
}
