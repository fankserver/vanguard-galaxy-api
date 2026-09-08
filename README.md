# VGModAPI

[![Core + Abstractions coverage](https://raw.githubusercontent.com/fankserver/vanguard-galaxy-api/coverage/badge.svg)](https://github.com/fankserver/vanguard-galaxy-api/actions/workflows/coverage.yml)

<p align="center">
  <img src="docs/assets/vgmodapi-logo.png" alt="VGModAPI logo" width="480">
</p>

Unofficial community mod API for Vanguard Galaxy, using BepInEx 5 and HarmonyX.

**Experimental; not fully runtime-qualified.** See [compatibility](docs/reference/compatibility.md) for the supported game build, available services, and in-game testing limits.

VGModAPI provides lifecycle events, mod save data, and optional mission, travel,
boarding, story, bar, recipe and mod-information services. It is an integration
layer, not a mod loader or a complete gameplay SDK.

## Install

1. Install BepInEx 5, close the game, and back up saves before changing plugins.
2. Verify the release ZIP against its accompanying SHA-256 file and extract its
   `VGModAPI/` folder into `<game>/BepInEx/plugins/`.
3. Keep one copy of `VGModAPI.dll`, `VGModAPI.Core.dll`, and
   `VGModAPI.Abstractions.dll`. Never replace the game's or BepInEx's assemblies.

Start with disposable saves. An unsupported game hash disables game integration;
do not override the compatibility gate. To uninstall, remove the API folder and
mods that require it, leaving saves and save-data sidecars intact.

## Use in a mod

Reference `VGModAPI.Abstractions.dll` as compile-only and declare a BepInEx dependency
on the minimum API version your mod uses:

```csharp
[BepInDependency(ModApi.PluginId, "0.1.0")]
```

In your plugin's `Awake`, inspect `ModApi.Current.Capabilities` and subscribe:

```csharp
_subscription = ModApi.Current!.Subscribe("your.mod.id", message =>
{
    Logger.LogInfo($"{message.Kind}: session={message.Session?.Id}");
});
```

Dispose subscriptions in `OnDestroy`. API access is main-thread-only; consumers
must handle unavailable optional services. Do not bundle the API assemblies with
your mod. API-covered features need no direct Harmony or vanilla assembly reference;
your BepInEx plugin entry point still needs BepInEx/Unity compile references.

The [API reference](docs/reference/README.md) describes each service's contract and
configuration. A compiled example is available in the source repository at
[`examples/LifecycleObserver`](https://github.com/fankserver/vanguard-galaxy-api/tree/main/examples/LifecycleObserver).

## Contribute

See the [developer documentation](https://github.com/fankserver/vanguard-galaxy-api/blob/main/docs/development/README.md)
and [roadmap](https://github.com/fankserver/vanguard-galaxy-api/issues/1).

## License

Owned source and documentation are [MIT licensed](LICENSE). Game, Unity, BepInEx,
and Harmony reference assemblies are not included in releases.
