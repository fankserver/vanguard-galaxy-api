# UI Surfaces

**What you can build with this: your own interface inside the game.** A real Unity window that lives
in the game's gameplay UI and belongs entirely to your mod, plus a Forge inspector that reads the
recipe catalog and shows requirements and output previews — added to the game's own Forge screen.

A sample/test BepInEx mod for the **VG Mod API** (v0.2.10+).

## What it demonstrates (the abilities)

| Ability | How the example uses it |
|---|---|
| **Gameplay UI containers** (`CreateContainer`) | Requests a consumer-owned container from the game's UI host and parents its own window into it. The API validates the expected host, including during reentrant notification delivery. |
| **Host lifetime** (`IGameplayUiService.Changed`) | Subscribes, then queries `Current`, so a consumer loaded *after* UI readiness still attaches. Host replacement detaches and re-attaches cleanly. |
| **Honest refusal** | A refused container is logged and dropped — no retries, guessed timeout, singleton lookup or Harmony patch. |
| **Consumer-owned content** | All layout, colours and text belong to the mod. Disposing the container destroys every child, so the window is never leaked or orphaned. |
| **Deferred creation** | The window is built on player input, not during readiness. |
| **Shared HUD** (`IHudService.Register`) | A HUD button toggles the window; the same shared HUD hosts the inspector's panel. |
| **Forge actions** (`IForgeUiService.RegisterAction`) | Adds an **Inspect** action to the Forge screen that captures the selected variant. |
| **Recipe quotes** (`IRecipeQuoteService`) | Shows requirements, accessible amounts and every output preview, with probability stated per batch. Quantities are **batches**, not a one-output assumption. |
| **Catalog reads** (`IRecipeService`) | Producer counts include alternative variants; the example does not choose a producer by name. |
| **Explicit overflow** | Above the HUD row limit it reports how many rows are not shown instead of silently truncating. |
| **Snapshot, not a pin** | The panel is refreshed by Inspect and cleared on session replacement; it issues no crafting commands and installs no save callbacks. |
| **Availability gating** | The inspector is created only when session, forge, recipe, quote and HUD services are all available, retried on a cheap tick. Missing services mean *unavailable*, not an empty successful catalog. |

## The files

| File | Role |
|---|---|
| `Plugin.cs` | BepInEx entry point: owns the Unity window and hosts the inspector. |
| `Inspector.cs` | The Forge inspector — **public contracts only, no Unity or BepInEx type**. |

`Inspector.cs` is deliberately Unity-free so it can be lifted into a non-Unity assembly; the
repository's host tests compile exactly that file without BepInEx, which keeps the property honest.
You do **not** need a separate host project to get this split — one package, two files.

## What you see

A HUD button (top-right, storage icon) toggles a consumer-owned window. On the Forge screen, an
**Inspect** action captures the selected variant into a shared HUD panel with requirements, producer
counts and output previews; its button navigates back to that exact variant and reports refusal
rather than substituting another.

## Build & deploy

```bash
dotnet build examples/UiSurfaces/UiSurfaces.csproj
```

Deploy `bin/Debug/netstandard2.1/UiSurfaces.dll` into `BepInEx/plugins/`. Do not deploy a duplicate
`VGModAPI.Abstractions` assembly alongside the separately installed API.

Requires the VG Mod API plugin (≥ **0.2.10**). This example is never part of the shipped API package.
