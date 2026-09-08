using System;
using System.IO;

namespace VGModAPI.Core.Integration;

internal sealed partial class WorldJsonInspection
{
    // Bounds apply before both immediate payload generation and deferred guard generation.
    private void CheckDescriptor(object descriptor)
    {
        var kind = Text(descriptor, "type");
        _nested.Descriptor(kind);
        if (kind == "FixedPayloadDescriptor")
        {
            Text(descriptor, "fixedUnit");
            Number(descriptor, "unitCount", 0, 128, true);
            OptionalNumber(descriptor, "overrideLevel", 1, 10000, true);
            OptionalNumber(descriptor, "bonusEquipChance", 0, 1, false);
        }
        else if (kind == "UnitPayloadDescriptor")
        {
            Number(descriptor, "pointsScale", 0, 100, false);
            var min = Number(descriptor, "minUnits", 0, 128, true);
            if (Number(descriptor, "maxUnits", 0, 128, true) < min) throw new InvalidDataException("Reversed unit count bounds.");
            Number(descriptor, "minPointsPerUnit", 0, 1000000, true);
            Number(descriptor, "maxPointsPerUnit", 0, 1000000, true);
        }
        else throw new InvalidDataException("Unsupported unit generation descriptor schema.");
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
