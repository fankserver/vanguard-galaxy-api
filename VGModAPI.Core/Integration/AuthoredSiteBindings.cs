using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Inspected authored-site creation boundaries. Declaring bindings does not install a world capability.</summary>
internal static class AuthoredSiteBindings
{
    internal const string System = "Source.Galaxy.SystemMapData";
    internal const string Element = "Source.Galaxy.MapElement";
    internal const string Poi = "Source.Galaxy.MapPointOfInterest";
    internal const string Salvage = "Source.Galaxy.POI.Salvage";
    internal const string Mining = "Source.Galaxy.POI.Mining";
    internal const string SalvageData = "Source.Data.Persistable.SalvageData";
    internal const string Persistable = "Source.Data.Persistable.PersistableData";
    internal const string HazardFieldData = "Source.Hazard.HazardFieldData";
    internal const string HazardData = "Source.Hazard.HazardData";
    internal const string HazardName = "Behaviour.Hazard.HazardName";
    internal const string DamageType = "Source.Combat.DamageType";
    internal const string DungeonType = "Source.Dungeon.DungeonType";
    internal const string DungeonLocationData = "Source.Data.Persistable.DungeonLocationData";
    internal const string AsteroidField = "Source.Mining.AsteroidFieldData";
    internal const string OreSet = "Source.Mining.AsteroidFieldOreSet";
    internal const string SeededRandom = "SeededRandom";
    internal const string SpaceShip = "Behaviour.Unit.SpaceShip";
    internal const string MiningHelper = "Source.Util.MiningPoiHelper";
    internal const string Faction = "Source.Galaxy.Faction";
    internal const string Vector2 = "UnityEngine.Vector2";

    internal static readonly MethodBinding[] Methods =
    {
        new("siteSetup", System, "SetupPOI", false, Element, Element, "System.Nullable`1<" + Vector2 + ">", Faction, "System.Int32"),
        new("siteShipExists", SpaceShip, "SpaceShipExists", true, "System.Boolean", "System.String"),
        new("siteWorldPosition", Poi, "GetWorldPosition", false, Vector2),
        new("siteAddPersistable", Poi, "AddPersistable", false, "UnityEngine.GameObject", "Source.Data.Persistable.PersistableData"),
        new("siteAddCargo", Poi, "AddCargoContainers", false, "System.Void", Vector2, "System.Int32", "System.Single"),
        new("siteAddScrap", SalvageData, "AddScrapContent", false, "System.Void", "System.Int32", "System.Single", "System.Int32", SeededRandom),
        new("siteAddStructural", SalvageData, "AddStructuralContent", false, "System.Void", "System.Int32", "System.Int32", "System.Single", SeededRandom),
        new("siteCreateHazard", Poi, "CreateHazardData", false, HazardData, HazardName, DamageType),
        new("siteHasDungeon", Poi, "HasExistingDungeon", false, "System.Boolean"),
        new("siteBuildDungeon", Poi, "BuildDungeonLocationData", false, DungeonLocationData, DungeonType, Faction, SeededRandom, "System.Boolean"),
        new("siteRollAbandoned", Poi, "RollIsAbandoned", true, "System.Boolean", DungeonType, SeededRandom),
        new("siteInitAsteroids", MiningHelper, "InitializeAsteroids", true, "System.Void", Poi, AsteroidField, "System.Boolean", "System.Boolean")
    };
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (System, "systemOreData", AsteroidField, false, false),
        (System, "pointsOfInterest", "System.Collections.Generic.List`1<" + Poi + ">", false, true),
        (Poi, "hazardFieldData", HazardFieldData, false, true),
        (Poi, "asteroidsInitialized", "System.Boolean", false, true),
        (Element, "level", "System.Int32", false, true),
        (Element, "faction", Faction, false, false),
        (SalvageData, "shipTemplate", "System.String", false, true),
        (Persistable, "angle", "System.Single", false, true),
        (Persistable, "position", Vector2, false, true),
        (Persistable, "hazardData", HazardData, false, true),
        (HazardFieldData, "hazardName", HazardName, false, true),
        (HazardFieldData, "damageType", DamageType, false, true),
        (HazardFieldData, "spawnChance", "System.Single", false, true),
        (AsteroidField, "density", "System.Single", false, false),
        (AsteroidField, "wealth", "System.Single", false, false),
        (AsteroidField, "surfaceOres", OreSet, false, true),
        (AsteroidField, "coreOres", OreSet, false, true)
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Methods);
    }
}
