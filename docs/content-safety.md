# Persistent content ownership and removal

`ContentSafety` (Abstractions, API 0.1.3) is a pure admission/recovery planner. It does not install content, intercept vanilla loads, rewrite saves or execute migrations. Callers must supply verified provider version/enabled state and compatible API/handler availability. Its actions select an explicit restoration procedure, not a guarantee that that procedure succeeds. Preserve original bytes on any failure.

## Ownership and admission

Every persistent reference carries an exact owner, local ID, kind and minimum provider version. The separately trusted declaration classifies its persistence impact. Identity matching is ordinal and kind-sensitive; paths and aliases are rejected. Unknown ownership or mismatched declarations are protected refusals, never another vanilla item/faction. A declaration must not be inferred solely from save-controlled fields.

Call `RequireAdmission` before accepting persistent content. API-dependent, provider-required and migration-only content require an explicitly acknowledged dependency. Future registry/factory APIs must enforce this boundary; the planner alone does not make arbitrary third-party content safe. Provider package metadata and feature availability must be verified before passing availability to `Assess`.

| Impact | Compatible enabled provider | Absent/disabled/removed provider |
|---|---|---|
| Independently reconstructable | Invoke provider | Use a retained independent handler only when explicitly available; otherwise require provider |
| API-dependent | Requires compatible API, then invoke provider | Requires compatible API and retained API reconstruction handler |
| Provider-required | Invoke provider | Restore/enable owning provider; refuse reinterpretation |
| Removal requires migration | Invoke provider | Require an explicit migration on a copy; do not delete references automatically |

Version comparison zero-fills omitted build/revision components (`1.0` equals `1.0.0.0`). Enabled-without-version input is a caller error. An installed version below the saved minimum is a downgrade refusal, not permission to reinterpret the state. A disabled provider is not an active factory. Unrecognized references remain opaque and protected even when no provider can describe them. `Diagnostic` provides user-facing action text; callers should include the owner/key and expose it before accepting a persistent dependency or attempting recovery.

Placeholders are permitted only as an explicit, independently retained reconstruction implementation that preserves identity and semantics. There is no generic vanilla-item/faction substitution or universal placeholder implementation here.

## Export and removal procedure

1. Close the game. Copy the vanilla save and metadata, the complete matching API generation/evidence directories, legacy sidecars and provider/version inventory into a separate private backup. Export opaque envelopes unchanged; do not trim unknown owners or omit intent/conflict evidence. Never publish these files by default.
2. Enumerate required content identities and dependencies. If ownership is unknown, restore the provider/catalog first. Merely removing the DLL is not migration.
3. Work only on a separate copy. A provider-specific migrator must explicitly resolve every reference (including nested missions, patrons, factions and world objects), preserve unrelated state and produce new associations. The current API supplies no generic vanilla-content migrator. If no supported migrator exists, retain the dependency; removal is unsupported and may be irreversible without the backup.
4. Validate the migrated copy with the provider disabled: load, save, reload, check economics/progression and all reference categories. Preserve before/after evidence. Only then intentionally adopt the new copy; retain the original backup and document the exact supported version range.

No universal safe-uninstall promise exists. Removing the API itself can strand API-dependent reconstruction as well as its hard-dependent consumers. Sidecar-only progression does not imply that vanilla world/type identities are provider-independent.

## Inspected vanilla boundary (game 0.8.2.3)

Assembly SHA256: `a2aad60bc68c31baccd636587d3c5ba4e651eacda59b0af42cd4f17f864284fb`.

| Saved reference | Inspected reconstruction boundary | Missing identity |
|---|---|---|
| Item | `InventoryItemType.Get` dictionary index | Throws; not an empty item |
| Story mission | `Mission.FromJson` string routes to `StoryMission.Get`; registry index | Throws; not a generic replacement mission |
| Patron | `BarPatron.FromJson` identifier routes to `Create`; type/constructor lookup | Missing type cannot construct patron |
| Faction | `Faction.Get` absent ID routes to type-based `Create` | Missing type fails, not another faction |
| World POI | `MapPointOfInterest.FromJson` type routes to `Create` | Missing type fails before world reconstruction |

Source-level motivating classifications (not new consumer migrations):

| Existing content | Conservative impact | Required action |
|---|---|---|
| CustomMission legacy faction IDs (`AbsolutionRogue`, `TerraformerColony`) retained by `LegacyFactionCompat` | Provider-required while those saved identities remain | Retain the compatibility provider; intentional removal requires a separate verified reference migration, not replacement with a vanilla faction |
| Silos runtime items inserted into `InventoryItemType.allItems` | Provider-required while inventories/world state reference their IDs | Restore Silos registration or retain the provider until an explicitly supported migration removes every reference on a copy |

These findings are scoped to the inspected assembly and named source paths, not all mod hooks. No external CustomMission, Silos or archived TravelJournal migration is performed.

## Fixtures and limits

Host fixtures serialize all five reference kinds and exercise absence, disablement, removal, downgrade, ownership mismatch, dependency admission and handler requirements while preserving fixture bytes. `-ContentReferenceProbe` additionally reads disposable reference files and probes the actual native lookup/factory boundaries with unique nonexistent identities. It must observe refusal and retain the files unchanged. This is not a complete foreign-content vanilla-save load or a real-world uninstall migration; those remain provider-specific validation requirements.
