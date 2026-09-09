# Authored dungeon content

`ModApi.Services.Dungeons` is a stable service for optional experimental content.
Inspect its typed `Availability` and per-call results before use. It initializes automatically when boarding observation, API save data and game
bindings are available.

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

## Installation reactions

Require API **0.2.10** when compiling against the current dungeon provider contract.
The dungeon content service must be available; unsupported game bindings cannot
provide installation events. Subscribe to a particular installation once during plugin setup:

```csharp
// After registering additional custom save data, if the mod has any.
var dungeons = ModApi.Services.Dungeons.AcquireProvider(PluginId, saveData: registration);
dungeons.GetInstallation("MyCampaignStationA").ExtractionStarted += AdvanceStationABeat;
dungeons.GetInstallation("MyCampaignStationB").ExtractionStarted += AdvanceStationBBeat;
// Dispose dungeons when the plugin stops.
```

`GetInstallation` returns the same non-null object for the same POI ID within a
provider. The installation need not exist yet. Subscriptions remain across
save/load, so subscribe before accepting a mission that will create the station;
no per-session rebinding or update loop is needed. This observes native POIs,
including ones created by another mod, and does not require an API-owned world
site or an attached custom dungeon definition. It neither creates content nor
claims ownership. Different providers may subscribe to the same installation.

Use a persistent POI identifier, not a display name. A known, stable authored ID
allows setup before creation. A POI with an engine-generated ID cannot be
subscribed to by a guessed name; the author must know its actual persistent ID.
An absent POI, or one without the matching installation dungeon location, raises
no events. If a handler never fires, first check that its ID exactly matches the
POI's persistent identifier; a typo is indistinguishable from content not yet created.
Lookup checks existing persistable membership and never generates
content or assumes the player is standing at the target.

`ExtractionStarted` means a successful native extraction request newly entered
its pending-extraction state. It is not victory, completed extraction or returned
crew. Repeated no-op requests, throwing requests and restoring an already pending
extraction do not replay it. Ship extraction does not raise installation events.

Handlers run at a later API gameplay boundary, outside observational callback
and save operations, and may directly advance story state or issue gameplay
commands. Commands still report their own readiness and results; this is not a
promise that every world/UI operation succeeds. If custom save data is supplied
once when acquiring the provider, delivery waits for its live mutation gate.
Without custom data, omit `saveData`; no dummy registration is needed.

Pending reactions are retained while the same session's custom data is blocked,
with a diagnostic for durable blocks; recovery allows delivery. Loss of session
or save observation also pauses delivery and produces a diagnostic rather than
guessing permission. Session end
cancels old reactions without rebinding them to another save. Subscriptions
remain available for future extractions. Removing a handler before delivery,
disposing its provider, or stopping the API suppresses pending callbacks.
Dispose the dungeon provider before its custom save-data registration. Disposing
the registration first closes delivery safely rather than invoking a handler
against disposed data; pending work waits with a diagnostic until the provider
is disposed or the session ends.

Handlers present at observation run in registration order, with each exception
isolated and logged. Newly added handlers do not receive old observations.
Reactions raised from another reaction wait for a later boundary rather than
recursing; one provider waiting for save data does not hold up another provider.
Handlers should be short and nonblocking. A throwing handler is not retried or
rolled back. The API does not promise to recover unsaved reactions after process
termination or to preserve arbitrary mod code as saved continuations.

The global `DungeonOperations.Changed` stream remains a low-level observational
interface. Use installation events for ordinary installation-specific reactions,
not a global handler that filters names or schedules its own later actions.

## Definitions and bounds

- Authored provider/local, compartment, event and choice IDs: 1–128 letters, digits, underscores, hyphens or dots; case-sensitive.
- Native item, crew and faction catalog keys: 1–128 characters, nonblank and without control characters. Spaces and punctuation are preserved verbatim (for example, `Titanium Plate`); registration still requires an exact native catalog match. Saved encoding and bounds are unchanged.
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
