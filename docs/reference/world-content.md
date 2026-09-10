# Owned world content boundary

`ModApi.Services.World` initializes automatically and remains a stable, non-null service. Provider authentication refuses when required bindings or persistence are unavailable. Register declarations before starting a session; create persistent Combat sites once the session and automatic persistence owners are ready.

## Declaration facade

Acquire `IWorldProvider` directly from the loaded plugin assembly and register immutable `WorldCombatSiteDefinition` values before starting a session. Definitions identify local content, revision, display name, an existing faction ID and level. Same-owner duplicate declarations are rejected; registration does not create a POI. The authenticated lease owns its declarations and must be disposed on provider teardown.

`CreateCombatSite(localId, occurrenceKey, systemId, x, y)` creates — or reconciles — an owned
persistent combat site in the current game, following the uniform occurrence contract shared with
authored systems: the consumer names the occurrence with an author-local key; the API allocates and
owns the native identity; no session tokens or instance GUIDs are supplied. Re-declaring the same
key returns the **same** `ICombatSite` object instance for the life of the session, never a
duplicate site. `GetCombatSite(localId, occurrenceKey)` re-obtains the occurrence in the current
game (null when it does not exist or the world cannot answer). Creation requires a ready session
and available persistence; the method returns null while the world cannot author — that is a
temporary refusal, not existence information.

`ICombatSite.State` reports typed occurrence state (`Reconstructed` with `PoiId`, or `Pending`);
`Changed` fires when the observed state transitions within its own session, for example when exact
native membership is lost or restored. `LastAction` retains the creation outcome. An occurrence
from an ended or replaced session keeps its last state and never resolves against the replacement
save — re-obtain objects for the live game. Existence still means exact current native membership:
an object in hand does not establish that the site is present right now; check `State`.

## Quiet authored locations

`ModApi.Services.World.AmbientTraffic` keeps an authored location visually quiet:
only the ships the story places there remain, while vanilla's decorative "passerby"
traffic is suppressed. Declare once, typically in `Awake`, and retain the returned
declaration for the mod's lifetime; dispose it to restore vanilla traffic.

```csharp
private IDisposable? _quietPocket;
private void Awake() =>
    _quietPocket = ModApi.Services.World.AmbientTraffic.SuppressInSystemContaining("my-authored-station");
private void OnDestroy() => _quietPocket?.Dispose();
```

- `SuppressAtStation(stationId)` quiets one station's decorative visitor ships. Use it
  for an authored station placed inside an ordinary populated system: gates and other
  stations in that system stay vanilla.
- `SuppressInSystemContaining(poiId)` quiets decorative station **and** jump-gate
  traffic in the single system containing the anchor location. Neighbouring systems
  stay vanilla: a suppressed gate's peer gate one jump away remains busy, so the
  contrast between an authored pocket and the ordinary galaxy is preserved.

Only the periodic decorative spawners are affected. Player docking, station services,
faction relations, security responses and any story-, mission- or consumer-placed
ships are untouched. Suppressed decorative ships are simply never created; nothing is
written to saves and no consumer save handling is needed.

Anchors are authored location identities, re-resolved against the current player's
galaxy on every decorative spawn. A declaration made before its authored content
exists stays inert and binds when the anchor appears, holds across save reloads, and
returns to inert if a loaded save lacks the anchor. When an anchor is missing or its
identity is ambiguous in the current galaxy, traffic everywhere stays vanilla rather
than being suppressed at a guessed location.

Declarations from different mods are independent; the same location may be declared
quiet by several mods, and disposing one declaration never releases another. Pass an
author-scoped `key:` to make a declaration replaceable: re-declaring the same key
replaces only your previous declaration (other mods' keys are a separate namespace),
removing any need for have-I-declared bookkeeping. Disposing a superseded handle is
inert and never revokes the replacement. Blank or
unbounded identities, wrong-thread access and use after API shutdown are programming
errors. `Availability` reports binding health for this integration; while it is
unavailable, declarations are retained but nothing is suppressed. Suppression
decisions fail open: an internal fault logs once and vanilla traffic proceeds.

## Protected story units

`ModApi.Services.World.UnitProtection` keeps an exactly identified, story-critical unit
alive for as long as the declaration is held — an allied ship moored as a narrative
anchor must never be lost to stray combat. Declare once with the unit's persistent
unit-data identity and retain the result; dispose it when the story no longer needs
the unit, restoring stock lethality.

```csharp
private IDisposable? _promise;
// The identity is the unit data's guid string, exactly as the game stores it.
private void Awake() => _promise = ModApi.Services.World.UnitProtection.Protect(myShipUnitId);
private void OnDestroy() => _promise?.Dispose();
```

Damage still plays out natively — impact effects, shield flashes, damage numbers and
reactions — but each hit's changes to the unit's recorded condition are restored when
the hit completes, and the native invincibility clamp is held only for the duration of
the damage call. The unit therefore reads as a normal, resilient friendly ship rather
than an obviously invulnerable object, can never be destroyed, and accumulates no
recorded hull, armor, shield, EMP or battle-damage changes. Purely visual surface
wear applied during a session may remain until the unit is next rebuilt from its
unchanged data, such as on save/load.

Each `Protect` call creates an independent declaration, so different mods can protect
the same unit without releasing each other. Declare once and retain the result rather
than re-declaring per frame or per resolution: repeated unkeyed calls accumulate
declarations for the service lifetime until each is disposed. Alternatively pass an
author-scoped `key:` — re-declaring the same key replaces only your previous
declaration, and disposing the superseded handle is inert. `Tune` on
`World.DroneBays` accepts the same `key:` parameter with the same semantics.

Protection is scoped to the persistent identity and needs no live instance: declaring
for persisted-but-not-yet-materialised unit data is supported and takes effect from
the unit's first damage event, leaving no unprotected window. It automatically covers
whichever live instance carries that identity after save/load or re-materialisation,
without consumer polling or frame-driven re-resolution. Nothing is written to unit data or saves: removing the
consumer restores completely vanilla behavior. A standing `isInvincible` flag set by
another mod is preserved, and a unit that was already destroyed is never resurrected.

Protection covers survivability only. Whether a heavily damaged unit may become a
boardable wreck remains a separate [boarding-rules](boarding-contract.md) decision;
declare both when a story unit must neither die nor become enterable. Blank or
unbounded identities, wrong-thread access and use after API shutdown are programming
errors. While `Availability` is unavailable, declarations are retained but stock
lethality applies; internal faults log once and fail open to vanilla damage.

## Encounter drone bays

`ModApi.Services.World.DroneBays` tunes how one exactly identified unit's drone bay
fights, for authored set-piece encounters. Identification is the unit's persistent
unit-data identity, so a ship of the same class — including the player's own — is
never affected, even when class identifiers collide.

```csharp
string bossUnitId = /* the boss's persistent unit-data guid string */;
_bossTuning = ModApi.Services.World.DroneBays.Tune(bossUnitId, new DroneBayTuning(
    launchSeconds: 0.05,                                   // the swarm is out in seconds, not minutes
    replacementDrones: new[] { "Combat Missile Drone", "Combat Laser Drone" },
    complement: 100));
// … when the encounter ends:
_bossTuning.Dispose();
```

- `launchSeconds` overrides the per-drone launch transition (stock 1.5) whenever that
  bay reads it. Only the declared bay is affected; disposal restores stock timing for
  later launches immediately.
- `replacementDrones` constrains what the bay reproduces when the game replaces
  losses, cycling the authored names deterministically so the encounter's composition
  holds for the whole fight. Every native roll for the declared bay uses the authored
  cycle, so previews of that bay's loadout reflect the same composition. Names are the game's drone catalog names; an unknown name
  is skipped with one report, and if none resolve the vanilla roll proceeds.
- `complement` rebuilds the bay's docked drones to the authored count through the
  game's own per-drone initialisation — hull multiplier, equipment, faction
  inheritance and all — in staggered batches across frames to avoid a hitch, then
  deploys. It applies once per session per unit; the game's own replacement logic
  maintains it afterwards. Declarations bind units that materialise later.

Only supplied aspects change; later declarations override earlier ones per aspect for
the same unit. Nothing is written to saves by this service, and an identity that is
missing or ambiguous in the world tunes nothing. Undeclared bays, integration faults
and unavailability all run completely vanilla, with one report per distinct cause.
This tunes the declared bay's behavior; it is not a general equipment-stat, loadout or
AI surface, and drone content itself remains the game's.

## Shared primitive selection

Two consumers motivate an owned encounter location, not a campaign framework:

- Anima's `MissionFactoryFromJson.BuildClearCombatSite` creates a Combat POI, attaches a mission objective and composes guards/reinforcement waves. Its fleet helpers request count ranges within point-budgeted generation.
- CustomMission's `SectorBuilder` builds pocket systems and gated entrances, chooses faction ships, and uses fixed payloads for exact-count timed spawns. Its scoped-hostility helper explicitly avoids changing global faction diplomacy.

The selected implementation boundary is a persistent, independently identified Combat POI in an existing system. Pocket-system and gate authoring are not implied by that selection. Six-act progression, boss escape, dungeon layouts, faction politics, autopilot rules and combat choreography remain consumer logic. A capability used by only one campaign detail is not sufficient justification for a shared API.

### Spawn semantics

Exact-count and point-budgeted operations must be distinct contracts:

- Native fixed payload creation constructs the requested number of one selected ship type.
- Native budgeted creation spends a total points budget and can return fewer units than a requested count. Its maximum may also expand from parent level and faction ship-budget calculations. Setting input count bounds is not an effective generation bound.
- Scoped per-unit hostility is not a change to the faction's global relationship with the player.

No public spawn operation is exposed. `UnitPayloadDescriptor` is refused by world inspection until effective generation bounds are validated. Fixed-descriptor input bounds and native selector checks are not complete validation of generated content.

## Declarative revision migration

Registration may supply one exact previous declaration. Supported migration advances the revision and may rename the site; local identity, faction and level must be unchanged. Matching only a previous revision number is insufficient: the complete retained declaration must match. During verified reconstruction, the API updates a native name only if it still equals the old declared default, preserving customized names and mutable instance level/state. The next automatic save records the current declaration/revision. Instance and mission-target identities do not change. Undeclared or incompatible older definitions remain refused; this is not arbitrary provider serialization or a general migration callback.

## Owned actor lifetime guards

Spawned units and persistable roots retain their originating manager/session; later activity does not borrow a replacement manager. Rejected captures remain classified. Unit initialization, damage, collision callbacks and selected coroutine continuations are guarded, and persistable updater writes require the original spawn-data reference. Known roots are rechecked during fixed updates, provider release, session changes and teardown. Refused live roots have their own rigidbody simulation and colliders disabled without traversing unrelated child actors.

Independently spawned equipment/projectiles and custom persistable subtypes are outside this API's supported creation contract. Disabling root physics is not a complete scene quarantine or safe-uninstall guarantee.

## Serialized native discriminator

Fresh owned Combat snapshots replace the native `Combat` type with `VGModAPIOwnedCombatV1` only after exact inventory association. Per-node digests and owner payloads are rebuilt after stamping, and the version barrier is retained. Verified loading keeps that discriminator unchanged and uses the JSON-bearing, single-use owned reader rather than native string-based type dispatch. Older owned `Combat`-shaped inputs and discriminator/identity mismatches are refused untouched. This prevents owned nodes from silently falling back to ordinary Combat deserialization; it is not a safe-uninstall claim.

## Persistence and activation constraints

Supported persistent creation must automatically preserve existence, owner/local identity, instance identity, supported properties, links and lifecycle state. Providers must not implement save hooks or rebuild timing for those fields. Temporary lifetime must be explicit; it must not replace persistence merely to avoid reconstruction.

The internal implementation pairs world inventory and full declarations in one committed save generation, associates them with the exact native snapshot and uses an API-required save-version envelope. Reconstruction requires the exact admitted constructor results, not just matching IDs. Two providers may declare the same local ID without sharing ownership; same-owner duplicate declarations are rejected.

Activation requires exact reconstruction or creation provenance and readiness of both automatic persistence owners. Load-time provider absence refuses before construction. No hot-unload guarantee is offered; retained refusal guards require process restart. Keeping immutable data after a provider lease is disposed is not, by itself, evidence that native activity is safe.

Nested state must satisfy the supported data-only contract at creation, save and load. Native type membership and a committed digest do not establish behavioral safety: triggered descriptors execute generation during loading, getters can generate deferred content, and scene/travel coroutines resume after their factories return. Executable provider payloads and custom native types/state cannot be admitted under a data-only lifetime contract.

Successful creation/lookup results carry a `PoiId` for `StoryObjective.TravelTo`. Reserved world targets are checked through the world's restored inventory and exact native membership, not the vanilla lookup alone. Story provider segments resolve to authenticated host plugin IDs; references to another world's owner are refused. These read-only checks work during `PlayerReady` after world reconstruction, before dependent story/bar restoration, while public creation remains blocked during dispatch. Missing or unready world dependency inspection is unknown, never proof of existence.

Mission references resolve after world reconstruction, with dependent story and bar restoration ordered accordingly. Save/load, cross-slot changes, save-as/rollback, failed saves, missing providers and declaration migration use the coordinated persistence and lifetime checks rather than provider-authored rebuild hooks.

## Authored pocket systems

`IWorldProvider` also authors enclosed pocket systems for bespoke encounters, alongside
the Combat-site surface. Register an immutable `AuthoredSystemDefinition` before starting
a session, then create the pocket for the current game with an author-local occurrence key.

```csharp
provider.RegisterAuthoredSystem(new AuthoredSystemDefinition("my-pocket", 1, "The Hollow"));
// Create (or reconcile) the owned occurrence for the current game; the SAME object instance is
// returned for the same key for the life of the session. Null only while the world cannot author.
var reference = provider.CreateAuthoredSystem("my-pocket", "act3-pocket", "anchorsystem-guid");
if (reference != null)
{
    reference.Changed += system => { /* this occurrence's State transitioned (e.g. Pending → Reconstructed on load) */ };
    var systemId = reference.SystemId;          // owned pocket system identity (travel target)
    var entrance = reference.EntranceGatePoiId; // anchor-side jump gate
    var pocketGate = reference.PocketGatePoiId; // pocket-side peer jump gate
}
```

A pocket system is anchored next to an existing system and created with **no storyteller**,
so vanilla generates nothing inside it; it is reachable only through its paired entrance
jump gate. The API creates the owned occurrence for the current game if it does not exist
and **owns every native identity** (system guid and both gate guids). You never supply a
native or instance GUID; you only name the occurrence with an author-local key.

Re-declaring the same key **reconciles to the owned occurrence** instead of creating a
duplicate. A foreign or ambiguous native identity is never adopted. The occurrence,
its gate pairing and its declarative gate state live inside the same sealed save envelope
as other owned world content, so ownership survives reload, save-as and rollback; a brand
new game starts with no bleed from an earlier one. The authored envelope shares the owned
save bound (1024 rows); creating past that bound is refused at Create time rather than
failing at save.

### Entrance gate state

Gate access is declarative and persisted as supported state — you do not need a per-frame
unhide/open repair loop:

```csharp
reference.SetEntranceOpen(open: true);   // opens both paired gates
reference.SetEntranceOpen(open: false);  // closes both
```

Opening unpairs and unhides **both** the entrance gate and the pocket-side peer together;
closing does the reverse. The declared state is a reconciled invariant: on load and on
later ticks the API re-applies it, so a drifted save converges back to the declared state.

### Reconstruction and failures

Each owned occurrence exposes typed per-occurrence reconciliation state, and its `Changed`
event fires for its own transitions (there is no keyed status query):

```csharp
var state = reference.State;
if (state.Reconstructed) { /* pocket is live with its native identity */ }
else if (state.Status == AuthoredSystemReconstructionStatus.Failed) { /* state.Reason explains why */ }

// A stale occurrence from a replaced/reloaded session fails its actions instead of touching the new save:
var result = reference.SetEntranceOpen(true);
if (result.Status == AuthoredActionStatus.GameEnded) { /* re-obtain for the live game */ }
```

After a reload the owned occurrences are re-obtained (no replay), mirroring how the API
hands back resolved content for the current game:

```csharp
foreach (var system in provider.GetAuthoredSystems("my-pocket")) { /* current-game occurrences */ }
var one = provider.GetAuthoredSystem("my-pocket", "act3-pocket"); // or a single key; null if not created yet
```

Reasons: `MissingDefinition`, `RevisionMismatch`, `NativeMissing`, `AmbiguousIdentity`,
`PersistenceUnavailable`. Excess occurrences converge or report; a pocket whose native
system cannot be located reports `NativeMissing`, and a re-declared definition whose
revision no longer matches the owned occurrence reports `RevisionMismatch`.
A retained row stamped with the immediately-previous declared revision migrates up to the
live definition automatically instead of failing — `RevisionMismatch` only fires when no
compatible previous declaration exists.

Once per session, at the post-reconstruction safe boundary, one
`AuthoredSystemReconstructionSettled` event reports the actual reconciliation outcomes,
carrying the owned occurrence objects (a failure carries its occurrence and reason; an empty
failure list means every declared occurrence reconstructed). Each occurrence's `Changed`
also fires for its own transitions. After that point you can still read each occurrence's
live `State` or re-obtain it.

Creation is gameplay-intent only; expected-session identity is an internal invariant.
`RegisterAuthoredSystem` is pre-session; `CreateAuthoredSystem`, `SetEntranceOpen` and the
re-obtain methods require a ready session.

## Authored wormhole pairs

Use an authored wormhole pair when two existing systems should be connected through the game's
native wormhole map icon, scene and travel flow rather than a jump gate. Register the immutable
definition before a session, then create the pair from ordinary gameplay logic:

```csharp
provider.RegisterAuthoredWormholePair(
    new AuthoredWormholePairDefinition("unstable-rift", 1, "Unstable Rift"));

var rift = provider.CreateAuthoredWormholePair(
    "unstable-rift", "daily-rift", originSystemId, pocket.SystemId!);

if (rift != null)
{
    rift.Changed += pair => { /* inspect pair.State */ };
    rift.SetOpen(false); // hides and disables both ends together
}
```

The API creates exactly two native `Wormhole` POIs and targets each end only at its owned peer.
The pair therefore never joins vanilla's untargeted global wormhole mesh and does not depend on
the player's global wormhole-unlock progression. Both systems must already exist and must be
different. Either endpoint may be an authored pocket system.

The API allocates both native POI identities, saves the system/POI association and declared open
state, and reconstructs it after load. `SetOpen` shows/enables or hides/disables both ends together
while retaining their exact pairing. Expiry remains consumer logic: a mod can close the pair on
its own clock. Reusing an occurrence key returns the same `IAuthoredWormholePair` object for that
session; re-obtain restored pairs with `GetAuthoredWormholePair` or `GetAuthoredWormholePairs`.
`AuthoredWormholePairReconstructionSettled` reports the provider's reconstructed and failed pair
objects once the world settles; each failed object's `State.Reason` gives the typed reason.

## Authored sites

Authored sites place campaign set-pieces inside an existing system — including an owned pocket
system — under the same occurrence contract as pocket systems and combat sites: author-local
occurrence keys, API-allocated native identity, same-key=same object per session, and typed
reconstruction state. The API persists the owned identity rows and re-resolves them per game;
the site's native content (wreck, station, asteroids) is persisted by the game's own
persistable pipeline. This replaces consumer-side per-galaxy cache-and-revalidate layers.

```csharp
provider.RegisterAuthoredSite(AuthoredSiteDefinition.Salvage(
    "failed-refuge", revision: 1, "Failed Industrial Refuge", level: 8,
    wreckShipId: "Monsoon", factionId: "Fanatics",
    withStation: true, hazard: AuthoredSiteHazard.DamageInRadius, scatterAsteroids: false));
provider.RegisterAuthoredSite(AuthoredSiteDefinition.MiningField(
    "singers-field", revision: 1, "Singer's Field", level: 8, asteroidCount: 6));

// From explicit gameplay logic, once the session is ready:
var site = provider.CreateAuthoredSite("failed-refuge", "act2-a", pocket.SystemId!, x: 10, y: 4);
var field = provider.CreateAuthoredSite("singers-field", "act4", pocket.SystemId!, x: -6, y: 2);
```

- A **salvage site** contains a defeated wreck of the exact declared ship class (an unknown
  class refuses creation — no silent template substitution), sized to the site level so it
  carries real salvage HP and material drops, with cargo containers nearby. `withStation: true`
  guarantees a boardable derelict station deterministically — no probability roll and no
  consumer retry loop; type and crewing keep the native distribution. The one supported hazard
  is a radiation cloud around the wreck. `scatterAsteroids` adds a decorative field around a
  bare wreck.
- A **mining field** contains exactly the declared asteroid count drawn from the host system's
  own ore data — the count is gameplay-relevant (objectives may count ore from it) and is not a
  probabilistic request.

Creation verifies the exact native membership delta (exactly one new POI, parented to the host
system, nothing removed) and never adopts a foreign or ambiguous native. A refused creation is
a typed `Rejected` retained on the returned object; it is not retried implicitly. `State`,
`Changed`, revision migration and the once-per-session `AuthoredSiteReconstructionSettled`
event follow the authored-system semantics above; site failures report `MissingDefinition`,
`RevisionMismatch`, `NativeMissing`, `AmbiguousIdentity` or `PersistenceUnavailable`.

## Moored authored ships

A moored authored ship is one friendly ship of an exact class held beside a station POI — a
narrative anchor like a permanently docked companion vessel — under the same occurrence contract.
The API owns its persistent unit identity (spawned through the game's own fixed-payload path so the
datum persists in the station's unit list), converges to exactly one instance by that identity, and
maintains the mooring idempotently while the station is the current POI: never boardable, no
docking state, no auto-AI, display name and commander callsign set to the declared name (the ship
class identity is never renamed), snapped to the authored offset with zeroed velocity.

```csharp
provider.RegisterAuthoredShip(new AuthoredShipDefinition(
    "promise", revision: 1, "Foundation's Promise", shipClassId: "Redemption",
    factionId: "Gold", offsetX: 24, offsetY: 30, protect: true));
var ship = provider.CreateAuthoredShip("promise", "act3", stationPoiId);
```

`protect: true` keeps the instance alive through unit protection, declared once per owned
occurrence (keyed, never accumulated) and re-declared automatically after restoration — covering a
persisted-but-not-yet-materialised ship from its first damage event. An unknown ship class or
faction refuses creation. `State`, `Changed`, revision migration and the once-per-session
`AuthoredShipReconstructionSettled` event follow the occurrence semantics above; a saved ship that
has not yet surfaced reports `NativeMissing` at settlement and converges with a `Changed`
transition when it does.

## Deterministic encounters

`provider.SpawnEncounter(poiId, composition)` schedules the exact ship counts an encounter was
designed around, routed through the game's timed-reinforcement trigger (point-budgeted payloads
under-place large requests, so exactness is the contract). All inputs — POI existence, every wave's
ship class, faction, rank — are validated before anything is scheduled; a refusal schedules
nothing, and a native under-schedule is an honest `Rejected` carrying the actual count.

```csharp
var result = provider.SpawnEncounter(sitePoiId, new EncounterComposition(
    new[] { new EncounterWave(15, "Monsoon", 10), new EncounterWave(45, "Monsoon", 10) },
    factionId: "Fanatics", level: 12, EncounterRank.Standard, hostileToPlayer: true));
```

`hostileToPlayer` scopes hostility to the spawned units only (`playerHostile` per unit) — an
existing faction's diplomacy and the player's reputation ledger are never modified, and
`noReputationLoss` (default) keeps kills reputation-free. Spawned units are transient session
content: the API does not persist, reconstruct or replay them, and the composition carries no
occurrence key. Re-spawning after a reload is explicit consumer gameplay logic.
