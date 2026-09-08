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
        new("worldElementWrite", "Source.Galaxy.MapElement", "ToJson", false, "LightJson.JsonValue"),
        new("worldRemove", "Source.Galaxy.SystemMapData", "RemovePointOfInterest", false, "System.Void", "Source.Galaxy.MapPointOfInterest"),
        new("worldCombatUpdate", "Source.Galaxy.POI.Combat", "AmbientUpdate", false, "System.Void", "System.Single")
    };
}
