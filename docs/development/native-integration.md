# Native integration constraints

These source-derived constraints explain the API's boundaries. They are not a complete game/mod audit or proof of runtime compatibility. The accepted original assembly identity and current qualification limits are in [compatibility](../reference/compatibility.md).

Inspect the original `Assembly-CSharp.dll` from `VanguardGalaxy_Data/Managed` in the local game installation. Temporary decompilation is disposable, not a source dependency. Do not commit decompiled game source. Reinspect the relevant implementation when changing hooks or supporting a different game hash.

Install coroutine factories before their callers so a missing hook cannot fabricate
completion. Match reflection signatures structurally, including generics, array ranks,
by-ref types and staticness; assembly-qualified generic spelling alone is unreliable.

## Save/load

Relevant types: `Source.Util.SaveGame`, `Source.Util.SaveGameFile`, `Source.Player.GamePlayer`, and `Behaviour.Bootstrap.SceneLoader`.

- `LoadSaveGame` starts `LoadSaveGameStaged()` as a coroutine and returns before loading completes.
- `LoadStateStaged` reconstructs the player through a nested coroutine, assigns `GamePlayer.current`, then initiates scene loading.
- Scene initialization is asynchronous and distinct from player readiness.
- `Store` can skip ephemeral players, catch failures, retry recursively and ultimately delete the failed file while returning normally.

A load-method postfix is not a gameplay-ready notification. A save-method postfix cannot alone distinguish skip, success and failure, and retries can invoke it repeatedly. Custom mission reconstruction also requires definitions to be available before deserialization; a late load event is insufficient.

See [lifecycle semantics](../reference/lifecycle-contract.md) and [story reconstruction](../reference/story-content.md).

## Mission transitions

Relevant type: `Source.MissionSystem.Mission`, together with the player's mission collection operations.

- The inspected `AddMissionWithLog` signature takes `(Mission, bool force = false)`. An optional argument is still part of the CLR signature.
- Acceptance can return early for duplicates.
- `CompleteMission` delegates to `ClaimRewards`, which can return without completion.
- Completion/archive callbacks can nest; failure can remove a mission and start a replacement.

Emit verified state transitions rather than method-call notifications. Keep narrative journal storage separate from shared lifecycle interpretation. See [mission events](../reference/mission-events.md).

## Travel

Relevant type: `Behaviour.Managers.TravelManager` and the concrete POI managers.

`JumpToSystem` changes the player's current system after several coroutine yields. Tutorial flow can replace the requested destination. POI arrival dispatch includes concrete overrides; inherited base dispatch needs separate consideration.

A request or departure is not an arrival. Load placement is not travel. Wormholes, route continuation, station transitions and replacement sessions require explicit attribution. See [travel events](../reference/travel-events.md) for supported paths and limits.

## UI

Vanilla canvas lifetime and scene readiness are distinct from player readiness. A `SidePanel.Start` attachment does not provide general layout coordination between mods. A shared HUD facility needs explicit attachment, disposal and placement rules rather than arbitrary pixel offsets.

The [mod-information menu](../reference/mod-information.md) is a specific main-menu integration, not a general HUD or vanilla-screen extension API.

## Item and recipe registration

Relevant types: `Behaviour.Item.InventoryItemType` and `Behaviour.Crafting.CraftingRecipe`.

Item and recipe loaders clear private dictionaries and repopulate them from Resources. `InventoryItemType.Get` directly indexes its registry. Consequently, content registration must account for reset order and missing references; inserting an item once is insufficient. Reusing a global category such as `UnusedMissionItem` can create cross-mod naming and ownership conflicts.

Stable ownership, collision checks, initialization order, reload behavior and [missing-content policy](../reference/content-safety.md) are prerequisites for safe registration. The pure content-safety planner does not itself install items or recipes.

## Campaign state and objectives

Installation-wide settings and individual save snapshots have different lifetimes. Storing progression in an installation-wide file, or merely moving it beside a save, does not establish rollback consistency. Vanilla missions/inventory and mod progression must refer to the same loaded snapshot.

Display descriptions and mutable list positions are unsuitable persistent objective identities. Scripted objectives need stable identity, post-load resolution, explicit completion and supported migrations. The closed [story subset](../reference/story-content.md) must not be described as a general scripted campaign API.

## Bar composition and content removal

An exclusive bar roster and additive patron contributions are conflicting policies even when patch order is deterministic. A composable API needs explicit contribution permissions and final-roster semantics; ordering alone cannot reconcile contradictory ownership rules.

Vanilla saves can retain provider-specific world/type identifiers independently of a mod's sidecar. Registering compatibility IDs or custom patron factories therefore does not establish safe uninstall. Provider removal needs explicit reference resolution and refusal behavior.

## Design boundary

A shared API centralizes version-sensitive integration; it does not eliminate it. Reliability depends on source-grounded semantics, compatibility gating, individual failure isolation and scoped native qualification. Keep bespoke gameplay in feature mods and avoid claiming unsupported modules from the existence of supporting lifecycle or storage infrastructure.
