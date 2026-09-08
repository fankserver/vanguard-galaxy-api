using BepInEx;
using VGModAPI;

namespace LifecycleObserver;

[BepInPlugin(Id, "VGModAPI Lifecycle Observer Example", "0.1.0")]
[BepInDependency(ModApi.PluginId)]
[BepInProcess("VanguardGalaxy.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Id = "vgmodapi.example.lifecycle";
    private ILifecycleService? _lifecycle;

    private void Awake()
    {
        _lifecycle = ModApi.Services.Lifecycle;
        _lifecycle.Changed += OnLifecycle;
        Logger.LogInfo($"Session tracking: {_lifecycle.SessionTracking.Availability.Reason}; save outcomes: {_lifecycle.SaveOutcomes.Availability.Reason}");
        Logger.LogInfo($"Initial session: {_lifecycle.CurrentSession?.Id}, phase: {_lifecycle.CurrentSession?.Phase}");
    }

    private void OnLifecycle(LifecycleEvent message)
    {
        Logger.LogInfo($"{message.Kind}: session={message.Session?.Id}, phase={message.Session?.Phase}, operation={message.OperationId}, destination={message.Destination}, detail={message.Detail}");
        // Observe only. Do not block, mutate an in-progress load/save, or retain vanilla references.
    }

    private void OnDestroy()
    {
        if (_lifecycle != null) _lifecycle.Changed -= OnLifecycle;
    }
}
