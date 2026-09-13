using System;
using BepInEx;
using VGModAPI;

namespace EWTest.Surity;

/// <summary>
/// Minimal load-bearing plugin for the EWTest.Surity test assembly. BepInEx only loads
/// assemblies that are plugins, and Surity discovers its [TestClass]/[Test] tests from
/// loaded assemblies -- so this no-op plugin exists so the test assembly is loaded and its
/// tests are found when the game is launched by the Surity CLI. Crash-free by design.
/// </summary>
[BepInPlugin(Id, "VGModAPI E2E tests (Surity)", "1.0.0")]
[BepInDependency(ModApi.PluginId)]
[BepInProcess("VanguardGalaxy.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Id = "vgmodapi.devtools.e2e.surity";

    /// <summary>Host authentication identity real consumers use; tests pass this to
    /// <see cref="IWorldService.AcquireProvider"/>.</summary>
    public static Plugin? Instance;

    private void Awake() => Instance = this;

    private void Start() { /* nothing to do; tests are driven by Surity */ }

    private void OnDestroy() { if (ReferenceEquals(Instance, this)) Instance = null; }
}
