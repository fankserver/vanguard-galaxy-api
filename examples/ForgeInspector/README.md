# Forge Inspector: independent author example

A Unity-free consumer of public VGModAPI contracts. It adds an **Inspect** Forge action and a shared HUD panel with selected-variant requirements, producer counts, and every output preview (explicit overflow summary above the HUD limit). It is independent of Blueprint Pin and does not copy its target policy.

Build with `make build-forge-example CONFIGURATION=Release`. The only project reference is Abstractions. No game, Unity, Harmony, Core or runtime implementation is referenced or packaged.

A BepInEx host with a hard dependency on `vgmodapi` can create this scope after the API initializes:

```csharp
// Keep this registration scope in a host field; dispose it during host teardown.
if (ModApi.Services.ForgeUi.Availability.IsAvailable && ModApi.Services.Recipes.Availability.IsAvailable &&
    ModApi.Services.RecipeQuotes.Availability.IsAvailable && ModApi.Services.Hud.Availability.IsAvailable)
    inspector = new ForgeInspector.Inspector(MyPluginId, ModApi.Services.Lifecycle,
        ModApi.Services.ForgeUi, ModApi.Services.Recipes, ModApi.Services.RecipeQuotes, ModApi.Services.Hud);
```

Enable `[Recipes] Enabled` and `[Hud] Enabled` and restart. Missing services mean unavailable, not an empty successful catalog. This helper is not itself a deployable BepInEx plugin; integrate its source or reference its assembly from your own host. Do not deploy a duplicate Abstractions assembly. A thin example host is provided in `../ForgeInspectorHost`; build it with `make build-forge-host CONFIGURATION=Release`. For an explicitly authorized sandbox install, copy only `ForgeInspectorHost.dll` and `ForgeInspector.dll` from that host's output alongside the separately installed API. The host uses Unity only for its lifecycle/retry tick; this library remains Unity-free.

The panel is deliberately a **snapshot** refreshed by Inspect, not a live pin. Quantities are batches, not a one-output assumption. Generated identities stay unknown; probabilistic quantities state the every-batch condition and probability. Inaccessible inventory is excluded explicitly. Producer counts include alternative variants; this example does not choose a producer by name. Its navigation button opens only the captured exact variant through the API and reports refusal. Session replacement clears captured state. It issues no crafting commands and installs no save callbacks.

For ongoing progress use `ICraftingJobService.Changed` and query restored jobs explicitly. Queue admission is not delivery: inspect each event's kind, remaining batches and delivery status before applying a consumer target policy. Blueprint Pin supplies the separately reviewed example of that policy. Native jobs persist without this helper saving anything.

Host tests verify description semantics, not Unity hover, layout, scaling, navigation or multi-output acceptance. Those runtime gates remain separate.
