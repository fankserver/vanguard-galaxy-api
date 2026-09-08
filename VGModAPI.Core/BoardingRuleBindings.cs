namespace VGModAPI.Core;

internal static class BoardingRuleBindings
{
    internal const string Sim = "Source.Dungeon.DungeonSimulation";
    internal const string Ship = "Behaviour.Unit.SpaceShip";
    internal const string Damage = "Behaviour.Weapons.DamageData";
    internal static readonly MethodBinding[] Hooks =
    {
        new("disable", Ship, "HandleBoardingCheck", false, "System.Boolean", Damage),
        new("scaling", Sim, "ApplyLevelScaling", false, "System.Void", "System.Int32", "System.Int32"),
        new("damage", Sim, "DamageFacility", false, "System.Void", "System.Single", "System.Boolean"),
        new("scuttle", Sim, "TryScuttle", false, "System.Void", "System.Single"),
        new("explosionTick", Sim, "TickExplosion", false, "System.Void", "System.Single"),
        new("explosion", Sim, "TriggerExplosion", false, "System.Void"),
        new("host", Sim, "NotifyHostDestroyed", false, "System.Void"),
        new("combat", Sim, "TickCompartmentCombat", false, "System.Void", "System.Int32", "System.Single"),
        new("ammo", Sim, "ApplyAmmoIntegrityDamagePerKill", false, "System.Void", "System.Int32"),
        new("hazard", Sim, "ApplyHazardFacilityDamage", false, "System.Void", "Source.CompartmentSystem.SimCompartmentData", "Source.CompartmentSystem.SimCompartmentEvent", "System.Int32"),
        new("grenade", Sim, "ThrowGrenade", false, "System.Boolean", "System.Int32"),
        new("createShip", Sim, "CreateForShipBoarding", true, Sim, BindingCatalog.BoardingLocation, BindingCatalog.BoardingOptions, "Behaviour.Dungeon.DungeonDefinition", "System.Single"),
        new("createWalk", BindingCatalog.BoardingOperation, "InitialiseWalkSimulation", false, "System.Void", "Source.Dungeon.DungeonData"),
        new("estimate", Sim, "EstimateFromData", true, "System.ValueTuple`4<System.Single,System.String,System.Int32,System.Int32>", BindingCatalog.BoardingLocation, BindingCatalog.BoardingOptions),
        new("estimatePower", Sim, "BuildEstimate", true, "System.ValueTuple`4<System.Single,System.String,System.Int32,System.Int32>", "System.Single", "System.Int32", "System.Single", "System.Int32")
    };
    internal static readonly MethodBinding[] Calls =
    {
        new("eligible", Ship, "CanBecomeBoardable", false, "System.Boolean"),
        new("convert", Ship, "BecameBoardable", false, "System.Void", Damage, "System.Single", "System.Single", "System.Single")
    };
    internal static readonly (string Key, string Type, string Name, string ValueType)[] Members =
    {
        ("hull", "Behaviour.Unit.AbstractUnit", "currentHullHP", "System.Single"),
        ("maxHull", "Behaviour.Unit.AbstractUnit", "maxHullHP", "System.Single"),
        ("unitData", "Behaviour.Unit.AbstractUnit", "unitData", "Source.Data.AbstractUnitData"),
        ("destroyed", "Behaviour.Weapons.TargetableUnit", "isDestroyed", "System.Boolean"),
        ("emp", "Source.Data.AbstractUnitData", "empCharge", "System.Single"),
        ("kind", Sim, "dungeonType", "Source.Dungeon.DungeonType"),
        ("level", Sim, "dungeonLevel", "System.Int32"),
        ("power", Sim, "levelDefenderPowerMod", "System.Single"),
        ("health", Sim, "levelDefenderHpMod", "System.Single"),
        ("integrity", Sim, "structureIntegrity", "System.Single"),
        ("location", BindingCatalog.BoardingOperation, "location", BindingCatalog.BoardingLocation),
        ("locationKind", BindingCatalog.BoardingLocation, "isShipBased", "System.Boolean"),
        ("locationLevel", BindingCatalog.BoardingLocation, "level", "System.Int32")
    };
}
