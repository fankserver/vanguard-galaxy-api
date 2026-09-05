# VGModAPI

Unofficial community mod API for Vanguard Galaxy, using BepInEx 5 and HarmonyX.

**0.1.0 experimental: implemented and automatically tested, not yet qualified in-game.** This is a core lifecycle foundation, not a complete modding SDK. No existing mods have been migrated.

## Implemented

- Runtime session identity, replacement/menu invalidation, player readiness, and gameplay-manager initialization.
- Coroutine-aware file-load observation and detected failure reporting.
- Save success/failure/skip outcomes, with recursive retries grouped into one operation.
- Main-thread-only disposable subscriptions, isolated subscriber exceptions, immutable event snapshots.
- Explicit capability availability and conservative installed-assembly compatibility gating.

`GameplayInitialized` does **not** mean every POI or UI is ready. Save success does not guarantee atomic sidecar persistence. Read the [lifecycle contract](docs/lifecycle-contract.md) before consuming events.

## Build and test

Requires .NET SDK 10 for the tests; the shipped libraries target `netstandard2.1`.

```sh
make build                         # refresh local BepInEx/Unity reference symlinks; build all projects
make test                          # pure tests; no game installation needed
make check-bindings                # inspect original installed game DLL without executing it
make package CONFIGURATION=Release # explicit three-assembly package in artifacts/VGModAPI/
```

Override `GAME_DIR` and/or `DOTNET` as needed. Game/Unity/BepInEx DLLs are never committed or bundled. The runtime binds game internals through inspected reflection; it does not require a publicized game stub. No serializer is needed by this milestone.

## Experimental installation

No automatic deploy target is provided. After approval for disposable-save testing, copy the generated `artifacts/VGModAPI/` folder into `<game>/BepInEx/plugins/`.

The package contains `VGModAPI.dll`, `VGModAPI.Core.dll`, and `VGModAPI.Abstractions.dll`, plus documentation. Keep one installed copy of these assemblies. An unsupported game hash leaves the service available for diagnostics but its lifecycle/save capabilities unavailable.

See [compatibility and qualification](docs/compatibility.md) for the inspected hash, completed checks, and pending in-game checklist.

## Consume

Reference `VGModAPI.Abstractions.dll` as compile-only and declare:

```csharp
[BepInDependency(ModApi.PluginId, "0.1.0")]
```

In your BepInEx plugin's Awake, inspect `ModApi.Current.Capabilities`, query `CurrentSession` if needed, and subscribe:

```csharp
_subscription = ModApi.Current!.Subscribe("your.mod.id", message =>
{
    Logger.LogInfo($"{message.Kind}: session={message.Session?.Id}");
});
```

Dispose the subscription in OnDestroy. All access is main-thread-only. Callbacks should observe, not block or mutate in-progress game operations. Do not ship a separate copy of the abstractions DLL with each consumer.

A complete compiled example lives at `examples/LifecycleObserver/` in the source checkout. It is built by `make build` but is not included in the API package. Its DLL can be installed separately for qualification event logging.

## Source layout

- `VGModAPI.Abstractions`: consumer contract; no Unity or vanilla types.
- `VGModAPI.Core`: internal state machines and reflection adapter; no Unity/BepInEx dependency.
- `VGModAPI`: loader plugin and Harmony hooks.
- `VGModAPI.Tests`: pure tests, adapter doubles, and installed-metadata checks.

[Implementation plan](docs/implementation-plan.md) · [Research findings](docs/research-findings.md)

Future optional modules cover persistence coordination, mission/story integration, bar policies, and HUD registration. Direct Harmony remains an escape hatch; bespoke gameplay stays in feature mods.
