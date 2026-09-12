# Cargo Recovery

**What you can build with this: your own boarding missions.** Board a derelict, walk your marines
through rooms you laid out, hit an authored decision in the cargo hold, and pull them back out —
with the outcome saved by the API, not by you.

A sample/test BepInEx mod for the **VG Mod API** (v0.2.10+). It is the full boarding tour: authored
content *plus* the optional contextual panel action, command lease, tactical request and observed
crew settlement that surround it.

## What it demonstrates (the abilities)

| Ability | How the example uses it |
|---|---|
| **Authored dungeon content** (`IDungeonProvider.Register`) | A three-compartment derelict: airlock → cargo hold (with a defender) → locked control room. |
| **Authored choices** | A discovered cargo-room event offering "Recover shipment" (crew-gated, yields loot) or "Leave shipment". Choice effects and occurrence state are saved by the API. |
| **Explicit attachment** (`Attach`) | The panel's **Attach cargo encounter** action is opt-in: it never attaches automatically on discovery or reload, and never replaces an existing attachment. |
| **Restored occurrences** (`SavedOccurrences`) | Reads API-restored occurrence state rather than keeping a private ledger. |
| **Contextual panel actions** (`IDungeonPanelService.RegisterAction`) | Per-target extraction control whose identity includes the target generation, so helpers for distinct targets coexist. |
| **Command leases** (`AcquireControl`) | Control is acquired **only on activation** and always released; another controller owning the target is reported as a typed refusal, never a forced takeover. |
| **Tactical requests** (`IDungeonTacticalService`) | Requests extraction only — confirmation, crew arrival and settlement stay separate concerns. |
| **Observed settlement** (`IDungeonSettlementService`) | Settlement observation is independent of panel visibility: closing the panel must not lose returning-crew facts. `CaptureApplied`, `CrewReturnSettled` and `CrewCountsObserved` mean different things; combat completion is not proof of crew delivery. |
| **Installation events** (`InstallationStory.cs`) | Subscribes to named campaign station installations once, with no update or session-reset loop. |
| **Capability gating** | Checks `DungeonPanel.Capabilities.ContextualActions` before wiring; accepting a registration alone does not prove the renderer exists. |
| **Lifetime discipline** | Per-target helpers are disposed on target retirement or session invalidation; the definition registration survives session replacement and is disposed only at plugin shutdown. |
| **Honest degradation** | Missing panel/command/tactics/settlement services leave the definition registered without contextual controls; missing boarding/content or blank reward configuration prevents registration entirely and logs why. |

## The files

| File | Role |
|---|---|
| `CargoEncounter.cs` | The authored encounter: layout, choices, attach/choose, restored occurrences. |
| `CargoEncounterPanel.cs` | Optional per-target controls: panel action, command lease, tactics, settlement. |
| `CargoAuthorSession.cs` | Main-thread consumer wiring, independent of the BepInEx loader. |
| `InstallationStory.cs` | Named-installation extraction events for campaign stations. |
| `Plugin.cs` | The thin BepInEx entry point that owns the session. |

`CargoEncounter.cs` and `CargoEncounterPanel.cs` deliberately touch **no Unity or BepInEx type** —
only public API contracts. The repository's host tests compile exactly those two files without
BepInEx, which keeps that property honest. Lift them into a non-Unity assembly if you want the same
split in your own mod; you do not need a separate host project to get it.

## Configuration (required)

Set `[Content] RewardItemId` in `vgmodapi.example.cargo.cfg` to an **existing** game item identifier
before a controlled run. Blank configuration deliberately registers nothing: the example does not
invent asset IDs or mutate player configuration. A catalog-validation failure gets one retry at
gameplay readiness; persistent failures log the item identifier and the catalog diagnostic. Restart
after correcting configuration.

## Lifetime note

Wiring happens in `Start()`, not `Awake()`. Dungeon content is keyed by a plain plugin-id string, so
`Awake()` would work here — but every example in this repository uses one consistent rule, because
the instance-authenticated services (world, story, bars, items, recipes) *cannot* be acquired in
`Awake()`: BepInEx only populates `Chainloader.PluginInfos[].Instance` after `Awake()` returns.

## Build & deploy

```bash
dotnet build examples/CargoRecovery/CargoRecovery.csproj
```

Deploy `bin/Debug/netstandard2.1/CargoRecovery.dll` into `BepInEx/plugins/`.

Requires the VG Mod API plugin (≥ **0.2.10**) and available boarding/dungeon services on the
inspected game build. No Unity polling, native casts, Harmony patches or custom save serializer are
used. This example is never part of the shipped API package.
