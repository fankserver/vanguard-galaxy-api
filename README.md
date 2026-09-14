# VGModAPI

[![Core + Abstractions coverage](https://raw.githubusercontent.com/fankserver/vanguard-galaxy-api/coverage/badge.svg)](https://github.com/fankserver/vanguard-galaxy-api/actions/workflows/coverage.yml)

<p align="center">
  <img src="docs/assets/vgmodapi-logo.png" alt="VGModAPI logo" width="480">
</p>

Unofficial community mod API for Vanguard Galaxy, using BepInEx 5 and HarmonyX.

**Experimental.** See [compatibility](docs/reference/compatibility.md) for the supported game build, available services, and current limitations.

VGModAPI provides lifecycle events, mod save data, and optional mission, travel,
boarding, story, bar, recipe, gameplay UI and mod-information services. It is an integration
layer, not a mod loader or a complete gameplay SDK.

## Install

1. Install BepInEx 5, close the game, and back up saves before changing plugins.
2. Verify the release ZIP against its accompanying SHA-256 file and extract its
   `VGModAPI/` folder into `<game>/BepInEx/plugins/`.
3. Keep the API DLLs together in that folder, without duplicate copies elsewhere.
   Never replace the game's or BepInEx's assemblies.

Start with disposable saves. An unsupported game hash disables game integration;
do not override the compatibility gate. To uninstall, remove the API folder and
mods that require it, leaving saves and save-data sidecars intact.

## Use in a mod

Reference `VGModAPI.Abstractions.dll` as compile-only and declare a BepInEx dependency
on the minimum API version your mod uses:

```csharp
[BepInDependency(ModApi.PluginId, "0.2.0")]
```

Retain the typed lifecycle service and subscribe in `Awake`:

```csharp
private ILifecycleService? _lifecycle;
private void Awake()
{
    _lifecycle = ModApi.Services.Lifecycle;
    _lifecycle.Changed += OnLifecycle;
}
private void OnLifecycle(LifecycleEvent message) =>
    Logger.LogInfo($"{message.Kind}: session={message.Session?.Id}");
private void OnDestroy()
{
    if (_lifecycle != null) _lifecycle.Changed -= OnLifecycle;
}
```

Inspect `SessionTracking.Availability` and `SaveOutcomes.Availability` before relying on these integrations. API access is main-thread-only; consumers
must handle unavailable optional services. Do not bundle the API assemblies with
your mod. API-covered features need no direct Harmony or vanilla assembly reference;
your BepInEx plugin entry point still needs BepInEx/Unity compile references.
For consumer-owned windows, also reference `VGModAPI.Unity.dll` and use the
[gameplay UI host](docs/reference/gameplay-ui.md); non-UI consumers need no bridge reference.

The [API reference](docs/reference/README.md) describes each service's contract and
configuration. A compiled example is available in the source repository at
[`examples/Observation`](https://github.com/fankserver/vanguard-galaxy-api/tree/main/examples/Observation).
For world creation — pocket systems, wormhole pairs, placement, quiet traffic, static names,
resource and combat sites and full cleanup — see
[`examples/PocketWorlds`](https://github.com/fankserver/vanguard-galaxy-api/tree/main/examples/PocketWorlds).
Every example package is listed in the
[examples index](https://github.com/fankserver/vanguard-galaxy-api/tree/main/examples/README.md).

## Contribute

See [Contributing](https://github.com/fankserver/vanguard-galaxy-api/blob/main/CONTRIBUTING.md) and the
[developer documentation](https://github.com/fankserver/vanguard-galaxy-api/blob/main/docs/development/README.md).

## License

Owned source and documentation are [MIT licensed](LICENSE). Game, Unity, BepInEx,
and Harmony reference assemblies are not included in releases.
