# Cargo Recovery

**What you can build with this: your own boarding missions.** Board a derelict, walk your marines
through rooms you laid out, hit an authored decision in the cargo hold, and pull them back out —
with the outcome saved by the API, not by you.

A sample/test BepInEx mod for the **VG Mod API** (v0.2.10+). It is the full boarding tour: it
**creates its own derelict**, attaches its own layout to it, and surrounds it with the optional
contextual panel action, command lease, tactical request and observed crew settlement.

## It is completely isolated

The example only ever touches the derelict it authored itself. It does **not** register contextual
actions on arbitrary observed targets, does **not** attach content to vanilla encounters, and never
takes command control of an operation it did not create. Vanilla boarding — and every other mod's
boarding — stays exactly vanilla. Everything it creates is removed again by one button.

Because it is about boarding, it initiates boarding fully: you do not have to go hunting for a
suitable vanilla derelict for the example to have anything to show.

## The flow

```
  Spawn derelict  ->  fly the gate  ->  station adopts the layout  ->  board
        |                                                               |
        +------------------  Remove derelict  <----  extract  <---------+
```

1. **Spawn derelict** creates a quiet owned pocket system beside your current one with its gate
   open, and an owned salvage site inside it declared `withStation: true` — which *guarantees* a
   native derelict station (research / relay / industrial). Not a probability roll.
2. The station is held **enterable**, so ambient world damage cannot destroy its docking or collapse
   its interior before you arrive. Unrelated stations stay entirely vanilla.
3. Fly the gate. As soon as a live boarding target belongs to that installation, the cargo layout
   **attaches itself** by installation identity. Until then the API answers `StaleTarget` — a
   temporary refusal that is simply retried, not a failure.
4. Board it, take the shipment choice, request extraction, watch settlement.
5. **Remove derelict** dissolves the pocket; the station and the site go with it.

## What it demonstrates (the abilities)

| Ability | How the example uses it |
|---|---|
| **Authored dungeon content** (`IDungeonProvider.Register`) | A three-compartment derelict: airlock → cargo hold (with a defender) → locked control room. |
| **Authored choices** | A discovered cargo-room event offering "Recover shipment" (crew-gated, yields loot) or "Leave shipment". Choice effects and occurrence state are saved by the API. |
| **Authored boarding target** (`CreateResourceSite` + `withStation`) | The example supplies its own derelict station rather than waiting for a suitable vanilla one. World creation is a separate service from dungeon content, so this composes the two. |
| **Held enterable** (`KeepEnterable`) | The authored objective cannot be invalidated by ambient damage before the player arrives; the hold is released on cleanup. |
| **Attachment by installation** (`Attach(IDungeonInstallation)`) | Attaches by persistent installation identity, which exists before any boarding target does — never by display-name matching. `StaleTarget` is treated as "not there yet", not as an error. |
| **Full cleanup** | One button dissolves the pocket, and the station and site go with it: authored sites have no `Dissolve` of their own. |
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
| `DerelictSite.cs` | Authors the boarding target itself: pocket system + salvage site with a guaranteed derelict station. |
| `CargoAuthorSession.cs` | Main-thread consumer wiring, and the isolation rule. |
| `InstallationStory.cs` | Named-installation extraction events for campaign stations. |
| `Plugin.cs` | The thin BepInEx entry point that owns the session. |

`CargoEncounter.cs` and `CargoEncounterPanel.cs` deliberately touch **no Unity or BepInEx type** —
only public API contracts. The repository's host tests compile exactly those two files without
BepInEx, which keeps that property honest. Lift them into a non-Unity assembly if you want the same
split in your own mod; you do not need a separate host project to get it.

## Configuration

`[Content] RewardItemId` in `vgmodapi.example.cargo.cfg` defaults to `SalvageCarbon`, a stock salvage
item. Item identifiers are **prefab names** under `Resources/Items`, not display names and not
material words — `Carbon` is not one, `SalvageCarbon` is.

Blank configuration deliberately registers nothing. A catalog-validation failure gets one retry at
gameplay readiness; persistent failures log the offending identifier and the catalog diagnostic, then
restart after correcting it. Successful registration logs `Cargo content registered; shipment reward = …`
so you can confirm it positively rather than by absence of a warning.

## Known API limitation

The public API cannot yet resolve an `IDungeonInstallation` to the live `BoardingHandle` it adopted.
The example therefore binds its optional per-target controls to the single observed target at
adoption time, and **skips those controls entirely when the target set is ambiguous** rather than
guessing — content still attaches. An `Attach` overload returning the resolved handle would remove
the compromise.

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
