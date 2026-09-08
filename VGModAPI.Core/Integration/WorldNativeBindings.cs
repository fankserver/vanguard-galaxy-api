namespace VGModAPI.Core.Integration;

/// <summary>Inspected serialization/construction boundaries. Declaring bindings does not install a world capability.</summary>
internal static class WorldNativeBindings
{
    internal static readonly MethodBinding[] Methods =
    {
        new("worldRecall", BindingCatalog.File, "Recall", false, "LightJson.JsonObject"),
        new("worldPoiRead", "Source.Galaxy.MapPointOfInterest", "FromJson", true, "Source.Galaxy.MapPointOfInterest", "LightJson.JsonValue"),
        new("worldSystemRead", "Source.Galaxy.SystemMapData", "FromJson", true, "Source.Galaxy.SystemMapData", "Source.Galaxy.SectorMapData", "LightJson.JsonValue"),
        new("worldSnapshot", BindingCatalog.Save, "SaveCurrentState", true, "LightJson.JsonObject"),
        new("worldStore", BindingCatalog.Save, "Store", true, "System.Void", "LightJson.JsonObject", "System.String", "Source.Util.SaveGameFormat", "System.Int32"),
        new("worldElementWrite", "Source.Galaxy.MapElement", "ToJson", false, "LightJson.JsonValue"),
        new("worldRemove", "Source.Galaxy.SystemMapData", "RemovePointOfInterest", false, "System.Void", "Source.Galaxy.MapPointOfInterest"),
        new("worldStartTravel", BindingCatalog.TravelManager, "StartTravel", false, "System.Collections.IEnumerator", "Source.Galaxy.MapPointOfInterest"),
        new("worldNextWaypoint", BindingCatalog.TravelManager, "TravelToNextWaypoint", false, "System.Void"),
        new("worldBaseAwake", "Behaviour.Managers.BasePoiManager", "Awake", false, "System.Void"),
        new("worldCombatAwake", "Behaviour.Combat.CombatManager", "Awake", false, "System.Void"),
        new("worldStoreLastX", "Behaviour.Managers.BasePoiManager", "StoreLastX", false, "System.Void"),
        new("worldStorePosition", "Behaviour.Managers.BasePoiManager", "StorePositionData", false, "System.Void"),
        new("worldIncomingReinforcements", "Behaviour.Managers.BasePoiManager", "CheckForIncomingReinforcements", false, "System.Void"),
        new("worldCreateSecurityPatrol", "Behaviour.Managers.BasePoiManager", "CreateSecurityPatrol", false, "System.Void"),
        new("worldManagerStart", "Behaviour.Managers.BasePoiManager", "Start", false, "System.Void"),
        new("worldManagerUpdate", "Behaviour.Managers.BasePoiManager", "Update", false, "System.Void"),
        new("worldSecurityPatrol", "Behaviour.Managers.BasePoiManager", "EvaluateSecurityPatrol", false, "System.Void"),
        new("worldManagerInit", "Behaviour.Managers.BasePoiManager", "Init", false, "System.Collections.IEnumerator"),
        new("worldInitializePoi", "Behaviour.Managers.BasePoiManager", "InitializePoi", false, "System.Collections.IEnumerator"),
        new("worldInitializationComplete", "Behaviour.Managers.BasePoiManager", "InitializationComplete", false, "System.Collections.IEnumerator"),
        new("worldBaseArrival", "Behaviour.Managers.BasePoiManager", "SpaceshipHasArrived", false, "System.Void"),
        new("worldCombatArrival", "Behaviour.Combat.CombatManager", "SpaceshipHasArrived", false, "System.Void"),
        new("worldSpawnPersistable", "Behaviour.Managers.BasePoiManager", "AddToWorld", false, "UnityEngine.GameObject", "Source.Data.Persistable.PersistableData"),
        new("worldSpawnUnit", "Behaviour.Managers.BasePoiManager", "AddToWorld", false, "Behaviour.Unit.AbstractUnit", "Source.Data.AbstractUnitData", "System.String", "System.Boolean"),
        new("worldActiveUpdate", "Source.Galaxy.MapPointOfInterest", "ActiveUpdate", false, "System.Void", "System.Single"),
        new("worldCanTravel", "Source.Galaxy.MapPointOfInterest", "CanTravelHere", false, "System.Boolean"),
        new("worldRoute", BindingCatalog.TravelManager, "SetRouteToPOI", false, "System.Boolean", "Source.Galaxy.MapPointOfInterest"),
        new("worldCombatUpdate", "Source.Galaxy.POI.Combat", "AmbientUpdate", false, "System.Void", "System.Single")
    };
}
