using BepInEx;

namespace VGModAPI.UpdateParticipant;

[BepInPlugin("vgmodapi.example.updates", "Update metadata example", PluginBuildVersion.Value)]
[BepInDependency(ModApi.PluginId, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    private void Awake() => Logger.LogInfo("Declared API dependency enables automatic listing. Optional sidecar metadata enables update participation; this plugin performs no networking.");
}
