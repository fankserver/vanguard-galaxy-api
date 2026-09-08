using System;
using System.IO;

namespace VGModAPI.Core.Integration;

internal sealed partial class WorldJsonInspection
{
    // Bounds apply before both immediate payload generation and deferred guard generation.
    private void CheckDescriptor(object descriptor, WorldNativeAssetInspection assets)
    {
        var kind = Text(descriptor, "type");
        _nested.Descriptor(kind);
        CheckAutoActions(descriptor);
        if (kind == "FixedPayloadDescriptor")
        {
            assets.Ship(Text(descriptor, "fixedUnit"));
            Number(descriptor, "unitCount", 0, 128, true);
            OptionalNumber(descriptor, "overrideLevel", 1, 10000, true);
            OptionalNumber(descriptor, "bonusEquipChance", 0, 1, false);
            OptionalEnum(descriptor, "rank", "Behaviour.Unit.UnitRank");
            OptionalEnum(descriptor, "loadout", "Source.Util.GameplayType");
            if (!(bool)_isNull.GetValue(Field(descriptor, "bonusEquipBuilderId"))!)
            {
                assets.Equipment(Text(descriptor, "bonusEquipBuilderId"));
                Number(descriptor, "bonusEquipChance", 0, 1, false);
            }
        }
        else if (kind == "UnitPayloadDescriptor")
        {
            // ResolveMaxUnits expands the requested count using level and faction budget.
            // Input bounds alone do not bound execution; refuse until that calculation is validated.
            throw new InvalidDataException("Budget-expanded unit generation is not admitted.");
        }
        else throw new InvalidDataException("Unsupported unit generation descriptor schema.");
    }
    private void OptionalEnum(object parent, string key, string typeName)
    {
        if (!(bool)_isNull.GetValue(Field(parent, key))!) _nested.EnumName(typeName, Text(parent, key));
    }
    private void OptionalNumber(object parent, string key, double min, double max, bool integer)
    {
        if (!(bool)_isNull.GetValue(Field(parent, key))!) Number(parent, key, min, max, integer);
    }
    private double Number(object parent, string key, double min, double max, bool integer)
    {
        var value = Field(parent, key);
        var type = value.GetType();
        if (!(bool)Property(type, "IsNumber").GetValue(value)!) throw new InvalidDataException("Expected native numeric field: " + key);
        var number = (double)Property(type, "AsNumber").GetValue(value)!;
        if (double.IsNaN(number) || double.IsInfinity(number) || number < min || number > max || (integer && Math.Truncate(number) != number))
            throw new InvalidDataException("Out-of-range native generation field: " + key);
        return number;
    }
}
