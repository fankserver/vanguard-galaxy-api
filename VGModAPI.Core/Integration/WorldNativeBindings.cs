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
        new("worldActiveUpdate", "Source.Galaxy.MapPointOfInterest", "ActiveUpdate", false, "System.Void", "System.Single"),
        new("worldCanTravel", "Source.Galaxy.MapPointOfInterest", "CanTravelHere", false, "System.Boolean"),
        new("worldRoute", BindingCatalog.TravelManager, "SetRouteToPOI", false, "System.Boolean", "Source.Galaxy.MapPointOfInterest"),
        new("worldCombatUpdate", "Source.Galaxy.POI.Combat", "AmbientUpdate", false, "System.Void", "System.Single")
    };
}
