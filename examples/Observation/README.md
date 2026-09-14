# Observation

**What you can build with this: mods that react to what the player is doing.** Watch sessions start
and end, missions change state and routes complete — without touching, blocking or corrupting any of
it. Plus the pattern for depending on this API *optionally*, so your mod still loads when it is absent.

A sample/test BepInEx mod for the **VG Mod API** (v0.2.10+), with a Unity-free consumer library
alongside it.

## What it demonstrates (the abilities)

| Ability | How the example uses it |
|---|---|
| **Session lifecycle** (`ILifecycleService.Changed`) | Logs every event with its phase, operation and detail, and reads `CurrentSession` for the live phase. |
| **Subscribe-then-read** | Subscribes *before* reading current state, so a mod loaded after readiness does not miss an existing session. |
| **Stale-fact rejection** | Mission transitions from a non-current session or a not-ready phase are ignored rather than acted on. |
| **Session-scoped travel** | Only travel transitions matching the travel service's own session id are counted. |
| **Terminal-fault handling** | `ObserverFault` / `ApiStopped` stop observation permanently — a terminal service fault cannot be repaired by loading a different save. |
| **Availability honesty** | Unavailable is reported with its reason; it is never rendered as "zero results". |
| **Clean teardown** | Every subscription is removed in `OnDestroy`. |
| **Additional custom save data** (`Consumers/CustomCounter.cs`) | Registers custom mod data before a session, observes provider state without re-registering, and distinguishes reading from mutation. Its four-byte serializer belongs to **that custom data**, not to API-owned content. |
| **Optional dependency** (`Consumers/OptionalTravelObserver.cs`) | No API types appear in its entry-point members; a loader supplies no resolver when the API is absent or out of range, and the typed access sits behind a guarded, non-inlined bridge. |
| **Injected domain logic** (`Consumers/MissionObserver.cs`) | Subscribes once, reads initial availability and unsubscribes after a terminal failure, without requiring saved identity merely to observe. |

## Observation is not the reaction model

The restrictions on this page belong to **low-level observational hooks**: observe only, do not
block, do not mutate an in-progress load/save, do not retain vanilla references.

They are deliberately *not* the model for domain events meant to drive gameplay. Those are delivered
at a safe boundary and support ordinary follow-up actions — see **StoryMissions** (a completed
mission offers its follow-up) and **CargoRecovery** (settlement and installation events).

## The two assemblies

| Project | Output | Why |
|---|---|---|
| `Observation.csproj` | `Observation.dll` | The loadable BepInEx plugin with the HUD panel. |
| `Consumers/Observation.Consumers.csproj` | `VGModAPI.ServiceConsumers.dll` | Plain .NET, **no BepInEx or Unity reference**. |

`Consumers/` stays a separate assembly on purpose: the repository's host tests load it into an
isolated load context that refuses `VGModAPI.Abstractions` resolution, proving the optional-dependency
entry point really exposes no API type in its members. Adding a BepInEx or Unity reference there would
invalidate that test. Those classes are **not** deployable BepInEx plugins — a plugin obtains its
root through `ModApi.Services` and injects these.

## What you see

A HUD panel counting lifecycle events, mission transitions and completed routes, with the last of
each. Full detail goes to `BepInEx/LogOutput.log`.

## Build & deploy

```bash
dotnet build examples/Observation/Observation.csproj
```

Deploy `bin/Debug/netstandard2.1/Observation.dll` into `BepInEx/plugins/`.

Requires the VG Mod API plugin (≥ **0.2.10**). See the
[service contract](../../docs/reference/service-contracts.md) for access, availability and lifetime
requirements. These examples are never part of the shipped API package.
