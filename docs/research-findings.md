# Research findings supporting the API

## Evidence limits

This assessment used source inspection, patch mapping, and fresh decompilation of selected classes from the installed original `Assembly-CSharp.dll`. No in-game compatibility test was performed. These observations are not a complete mod audit.

The installed DLL was at:

```text
/mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy/VanguardGalaxy_Data/Managed/Assembly-CSharp.dll
```

Freshly inspected types: `Source.Util.SaveGame`, `Source.Util.SaveGameFile`, `Source.Player.GamePlayer`, `Source.MissionSystem.Mission`, `Behaviour.Managers.TravelManager`, `Behaviour.Bootstrap.SceneLoader`, `Behaviour.Item.InventoryItemType`, and `Behaviour.Crafting.CraftingRecipe`.

Temporary decompilation is under `/tmp/vg-api-review/`; it is disposable, not a durable source dependency. Reinspect the installed DLL before implementation. Do not commit decompiled game source.

## Save/load: strongest shared boundary

Anima, MissionJournal, TravelJournal, Stockpile, and Silos independently patch `SaveGameFile.LoadSaveGame` and `SaveGame.Store`.

Installed behavior:

- `LoadSaveGame` starts `LoadSaveGameStaged()` as a coroutine and returns before full loading completes.
- `LoadStateStaged` reconstructs the player through a nested coroutine, assigns `GamePlayer.current`, then initiates scene loading.
- Scene initialization is asynchronous and distinct from player readiness.
- `Store` can skip ephemeral players, catch failures, retry recursively, and ultimately delete the failed file while returning normally.

Consequences:

- A load-method postfix is not a gameplay-ready notification.
- Early sidecar loading may be appropriate, but must not imply player readiness.
- A save-method postfix cannot alone distinguish skip, success, and failure and may execute repeatedly through retries.
- Anima needs pre-deserialization registration for custom mission reconstruction; a single late load event is insufficient.

Sources: sibling `*/Patches/SaveLoadPatch.cs`, Silos `Patches/SaveLoadPatches.cs`, Anima load registration, and fresh installed decompilation.

## Mission transitions

Anima and MissionJournal share acceptance, completion, failure, archive, and removal hooks.

- MissionJournal coordinates nested completion/archive callbacks to avoid duplicate history.
- Both source trees explicitly target `AddMissionWithLog(Mission)`; the inspected installed game instead declares `AddMissionWithLog(Mission, bool force = false)`. An optional argument does not retain the old CLR signature.
- Acceptance can early-return for duplicates.
- `CompleteMission` delegates to `ClaimRewards`, which can return without completion.
- Failure can remove a mission and start a replacement.

API implication: emit verified state transitions, not method-call notifications. Keep journal history storage separate from shared lifecycle interpretation.

## Travel

TravelJournal records jumpgate transit from a prefix on `JumpToSystem`. The installed coroutine updates the player's current system after several yields and includes a tutorial transition that can replace the destination.

TravelJournal also dynamically patches concrete POI-arrival overrides to avoid duplicate base/override notifications, with a documented gap for future subclasses inheriting the base implementation unchanged.

API implication: distinguish departure/request from actual arrival and define coverage for wormholes, save placement, and other transition paths.

## UI duplication

Stockpile and ItemCompass both hook `SidePanel.Start` for canvas attachment. ItemCompass explicitly adapts Stockpile's icon, window, map-focus, and jump-distance patterns. Icons use configured pixel offsets; Stockpile offsets its second icon by another 48 pixels.

API implication: a small shared HUD registration/layout surface has demonstrated consumers. Do not generalize every vanilla UI screen.

## Item/recipe registration

Silos creates 26 runtime item GameObjects, sets private backing fields, inserts into vanilla registries, and rebuilds after catalog resets. Installed item and recipe loaders clear private dictionaries and populate from Resources.

Silos repurposes `UnusedMissionItem` and globally relabels that category. Independent mods making the same choice could interfere. Vanilla `InventoryItemType.Get` directly indexes the registry, so missing content requires explicit save-safety handling.

API implication: ownership, namespaced IDs, collision checks, initialization order, reload behavior, and missing-content policy must precede a stable content API.

## External consumer: VanguardGalaxy-CustomMission

Source tree: `../VanguardGalaxy-CustomMission/plugin/`.

This is a six-act scripted campaign, unlike Anima's generated broker jobs. It independently demonstrates demand for reusable story infrastructure.

### Lifecycle

`Shared/GalaxyRuntime.cs` combines a staged-load prefix, per-frame player/map identity checks, and resets for all act/sector caches. It correctly recognizes coroutine timing. The source documents stale references across galaxy reloads as its motivation.

### Persistence

`Shared/StoryProgress.cs` stores progress at `BepInEx/config/com.vanguardgalaxy.custommission/story_progress.txt`. `INSTALL.md` explicitly documents that it is shared across save files. Some step counters are treated as authoritative while vanilla separately stores missions and inventory.

Implication: distinguish campaign state from installation settings and individual save snapshots. Moving a sidecar beside a save alone does not guarantee rollback consistency.

### Objectives

`Shared/StoryObjectives.cs` replaces historical dummy kill objectives with vanilla trigger objectives and completes them programmatically. Live resolution uses step positions and, for some hooks, description text.

Implication: stable objective identity, live resolution after reload, explicit completion, and content migrations are better initial abstractions than a universal mission-authoring DSL.

### Bar composition conflict

`BarPatrons/BarPatrons.cs` patches `Bar.CheckUpdatePatrons` and removes every non-owned patron at Foundation Station. Anima contributes patrons at the same vanilla boundary, while TTS observes roster changes. Anima already has explicit before/after ordering relative to TTS.

Conditional source-level conflict: if Anima contributes at Foundation, CustomMission may remove its broker or Anima may violate CustomMission's four-patron policy, depending on execution order. Not reproduced in-game.

Implication: ordered phases alone cannot resolve contradictory ownership policies. Define exclusive versus additive bars, contribution permissions, and final-roster notifications.

### Saved content compatibility

`Shared/LegacyFactionCompat.cs` registers historical faction IDs to preserve older-save loading. Custom patron reconstruction intercepts `BarPatron.Create` for its own types.

Implication: sidecar-based progression does not imply safe uninstall; vanilla saves can retain custom world/type identifiers. Content registration needs ownership and explicit provider-removal expectations.

## Recommendation

Build a small core lifecycle API first, with optional story/UI/content modules following verified consumer demand. Use Anima and CustomMission as contrasting story consumers. Keep combat rules, boss choreography, and other bespoke behavior in individual mods.

A shared API relocates fragility; it earns reliability through semantic validation, compatibility checks, isolated failures, and live qualification—not merely through fewer Harmony patches.
