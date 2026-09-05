using System;
using BepInEx;
using VGModAPI;

namespace LifecycleObserver;

[BepInPlugin(Id, "VGModAPI Lifecycle Observer Example", "0.1.0")]
[BepInDependency(ModApi.PluginId, "0.1.0")]
[BepInProcess("VanguardGalaxy.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Id = "vgmodapi.example.lifecycle";
    private IDisposable? _subscription;

    private void Awake()
    {
        var api = ModApi.Current;
        if (api == null) { Logger.LogError("VGModAPI service unavailable."); return; }
        foreach (var capability in api.Capabilities)
            Logger.LogInfo($"{capability.Name}: available={capability.Available}, qualified={capability.RuntimeQualified}: {capability.Detail}");
        Logger.LogInfo($"Initial session: {api.CurrentSession?.Id}, phase: {api.CurrentSession?.Phase}");
        _subscription = api.Subscribe(Id, message =>
        {
            Logger.LogInfo($"{message.Kind}: session={message.Session?.Id}, phase={message.Session?.Phase}, operation={message.OperationId}, destination={message.Destination}, detail={message.Detail}");
            // Observe only. Do not block, mutate an in-progress load/save, or keep vanilla references here.
        });
    }

    private void OnDestroy() => _subscription?.Dispose();
}
