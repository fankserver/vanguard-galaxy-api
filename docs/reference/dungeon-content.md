# Authored dungeon content

`ModApi.Services.Dungeons` requires API 0.1.30 and is optional and experimental. Query the `dungeon-content`
capability before use. It requires boarding observation, API save data and
`Dungeons.Enabled`; it is disabled by default. Host tests and binding checks do
not establish in-game qualification.

A provider registers immutable `DungeonDefinition` values under its plugin ID
and a local ID. Another provider may reuse the local ID. Dispose registrations
to remove available definitions; dispose the provider to revoke its behavior.
Existing saved occurrences retain their definition snapshots rather than being
rewritten by a replacement registration.

`Attach` creates an independently identified persistent occurrence on an
existing, observed target. It refuses targets with a current operation or saved
simulation. This does not create a new world object or POI: world creation belongs
to the world/POI service, not a second dungeon-owned world registry. Target handles
are session-local; occurrence GUIDs survive save/load.

## Definitions and bounds

- IDs: 1–128 letters, digits, underscores, hyphens or dots; case-sensitive.
- Name: at most 256 characters; positive definition version.
- Layout: 2–64 connected compartments, symmetric adjacency, at most eight
  neighbors per room, exactly one unlocked airlock. Native placement puts the
  airlock at index zero even when author input uses another order.
- Defenders: at most 32 crew types and 100 defenders per room, 512 overall.
  Attachment additionally enforces the target's native compartment capacity.
- Events: at most 128 unique IDs referencing existing compartments; text at most
  4,000 characters; 1–8 unique choices per event.
- Choices: text at most 1,000 characters; optional required native crew ID;
  at most 16 item rewards, each with an amount from 1 to 10,000.
- Retained definition: at most 128 KiB. Entire API-owned dungeon state: at most
  1 MiB and 256 occurrences. Creation and choices check the complete retained
  payload before changing it; content is not silently truncated.

Crew, item and faction IDs refer to existing native catalogs. Strings cannot
create new crew types, items, recipes or factions. Item/recipe authoring remains
separate from selecting an existing item as dungeon loot. The optional faction
sets the simulation's boarding profile, not ownership of the host world object.
Native no-scuttle restrictions remain applicable.

`AllowHazards` controls native hazard events. `AllowScheduledReinforcements`
controls native scheduled reinforcements, not incoming transports already owed
crew. These rules are retained with each occurrence.

## Choices, ownership and save/load

Only the acquiring provider instance can choose for its occurrences. The named
compartment must be friendly-controlled and have living friendly crew; a
specialist choice additionally requires that crew type in the compartment.
Optional allow callbacks receive copied context, cannot reenter content mutation,
and fail closed when they throw. Disposing behavior during its callback revokes
that pending choice.

A choice is recorded before native effects. Native exceptions propagate; a
possibly partially applied choice is not automatically retried. A recorded choice
is therefore **not proof of successful reward delivery**. Loot enters vanilla's
collected-loot queues and follows extraction and settlement, rather than bypassing
them with direct inventory grants.

The API registers its own save participant. Consumers do not supply save callbacks
or serializers for this content. Creation is refused when persistence is absent,
unrestored or not writable. Each native location stores only an occurrence marker;
definitions and choices live in the API-owned envelope. Duplicate location markers
block authored execution rather than selecting an arbitrary copy.

Missing providers do not erase retained definitions. Choosing requires an active
matching local definition and the same definition version. There is no implicit
migration to a newer registered version. Changing a version does not rewrite
existing occurrences; an incompatible choice request returns `VersionMismatch`.
Session changes invalidate native mappings and hide previous-slot state until the
current slot has restored.

`ModApi.Services.Dungeons` exposes a stable `IDungeonContentService` with typed
`Availability` and `AvailabilityChanged`. Missing catalogs refuse provider acquisition
without placeholder native definitions. Health loss refuses attachment and choice
mutation without altering saved occurrences. Provider ownership and save-data
readiness remain independent checks; health alone never authorizes persistence.
