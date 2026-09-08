namespace VGModAPI.Core;

internal static class DungeonRecoveryMarkerBindings
{
    internal static readonly MethodBinding[] Hooks =
    {
        new("recoveryPodSave", DungeonPodResumeBindings.Data, "DataToJson", false, "System.Void", "LightJson.JsonObject"),
        new("recoveryPodLoad", DungeonPodResumeBindings.Data, "LoadFromJson", false, "System.Void", "LightJson.JsonObject"),
        new("recoveryLocationSave", BindingCatalog.BoardingLocation, "DataToJson", false, "System.Void", "LightJson.JsonObject"),
        new("recoveryLocationLoad", BindingCatalog.BoardingLocation, "LoadFromJson", false, "System.Void", "LightJson.JsonObject")
    };
}
