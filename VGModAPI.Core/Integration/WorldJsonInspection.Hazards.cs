namespace VGModAPI.Core.Integration;

internal sealed partial class WorldJsonInspection
{
    private void CheckHazard(object hazard)
    {
        _nested.Hazard(Text(hazard, "hazard"));
        _nested.EnumName("Source.Combat.DamageType", Text(hazard, "damageType"));
        Number(hazard, "damageMultiplier", 0, float.MaxValue, false);
        Number(hazard, "maxDamageFalloff", 0, 1, false);
        Number(hazard, "range", 0, float.MaxValue, false);
    }
    private void CheckHazardField(object field)
    {
        Number(field, "generalChance", 0, 1, false);
        _nested.EnumName("Behaviour.Hazard.HazardName", Text(field, "hazardName"));
        _nested.EnumName("Source.Combat.DamageType", Text(field, "damageType"));
    }
}
