# Owned bar rosters

Enable `[Bars] Enabled = true`; API-managed saves initialize automatically. Bars are opt-in; `ModApi.Services.Bars` is a stable `IBarService`; inspect typed `Availability` or observe `AvailabilityChanged`. The unavailable instance does not register a replacement save owner.

## Provider lifecycle

Acquire a provider from the loaded BepInEx plugin instance in `Start` with `ModApi.Services.Bars.AcquireProvider(this)`. Chainloader publishes its authenticated instance only after `Awake` returns; acquiring during `Awake` is refused. Consumers can latch managed mode in `Awake` to prevent a native fallback before acquisition. The API authenticates its stable owner identity. Pass an optional owned custom-save registration to `AcquireProvider(this, saveData: registration)` to make it a prerequisite for placements and interaction delivery.

Declare contacts with `Register(new BarPatronDefinition(...), patron => ...)`. The definition contains a local ID, native station GUID, display name, description and seed. Different providers may reuse local IDs. Registration returns an `IBarPatronDefinition` handle. The optional `interact` argument and the handle's `Interacted` event are the **same** subscription list: `Register(definition, handler)` simply adds the handler to `Interacted`. Use the argument for the common case; use `Interacted +=`/`-=` only to add or remove handlers later. Wiring both delivers the interaction twice. Re-registering the same local ID **replaces** this provider's declaration: the old handle's queued interactions are dropped and disposing the old handle does not revoke the replacement or any other provider's contact. Dispose the handle (or the provider) to withdraw the declaration.

Presentation attributes (seed, portrait, seated body) can be grouped in a
`BarPatronPresentation(seed, portrait, isMale)`; the flat constructor overload with named
`portrait:`/`isMale:` arguments remains available. Use `CharacterPortrait.Named("M2Captain")` to request specific NPC portrait art, or
`CharacterPortrait.OfCharacter("LuminateCommander")` to borrow a game character's portrait.
Set `isMale: false` for the native female seated body (`true` is the default). Vanilla selects
body sprites using `IsMale` independently of the seed; neither gender nor portrait is inferred from
the other. The seed remains a separate presentation input, not a portrait or gender selector.
Omitting the portrait retains the default contact icon. An unresolved explicit portrait leaves the
contact usable without an icon and reports the missing identity once. Rebuilt contacts resolve it
again, so an initially unavailable art catalog is not cached as a permanent failure.

Portrait identities are saved with persistent patrons, not Unity objects. Schema-1 patron data
remains readable with its default portrait; schema-2 writes preserve the existing provider and
payload limits, including portrait bytes.

## Automatic placement and live patrons

Consumers do not place contacts per session. The API places each registered declaration automatically once the game and its save data are ready, and reconstructs saved presentation on load. Successful placement is **not a visibility guarantee**: station policy, native capacity, readiness and dependencies determine admission at refresh.

`game.Bars.Get(definition)` returns the live patron for a captured game. `Status` reports `Waiting`, `Assigned`, `Removed`, `Unavailable` or `GameEnded`; `Changed` fires as a safe gameplay reaction. `Remove()` hides the patron in that game and the absence itself is saved — reloading does not resurrect a removed contact, and re-registering the definition does not either. `Restore()` reinstates the saved presentation (never startup defaults) or places the contact for the first time. Both actions return retained results that progress `Queued → Succeeded/Rejected/Unavailable/GameEnded`; a result queued into a replaced save ends `GameEnded` without another tick, and old game objects cannot act against a replacement save.

Interaction callbacks receive the live `IBarPatron`, so `patron.Game`, `patron.Status` and actions such as `patron.Remove()` are directly available. Delivery is deferred to a safe boundary and only occurs while the definition, session, roster admission and save prerequisites are still current.

## Station policy and capacity

`ConfigureStation(stationGuid, BarRosterOwnership.Additive)` contributes without evicting vanilla patrons. Native presentation has five seats; insufficient free capacity can deny contributions.

Exclusive ownership requires the exact plugin ID in `[Bars] ExclusiveProviders` (comma-separated). An authorized exclusive provider suppresses vanilla and other providers at that station; it does not delete the retained vanilla roster. Conflicting authorized exclusive claims deny every contribution and retain vanilla rather than selecting the last Harmony patch. Permission revocation is rechecked before application and interaction.

Policies are runtime registrations. They do not silently follow a provider that has been removed. The finalized observation reports denied providers and reasons. Providers should not bypass a refusal by mutating the native roster themselves.

## Refresh, observation and interaction

The API owns the sequence: native refresh, contribution admission/application, then finalized observation. `RosterFinalized += handler` registers without replay; remove the exact handler with `RosterFinalized -= handler` during teardown. Its immutable snapshot identifies the session, station, seats, seeds, native kinds, owned IDs and denied providers. A cache observer must use this final membership, not an intermediate native initialization list. Subscriber failures are isolated and diagnosed.

Interactions are dispatched to the current owner's callback only while the contact, session, policy and dependencies remain valid. Narrative dialogue, mission authoring and voice synthesis remain consumer responsibilities. The shared service does not persist callbacks, audio, UI objects or native handles.

Unknown modifications to a retained native roster are refused rather than silently losing suppressed vanilla patrons. On shutdown the adapter restores tracked native rosters when safe; process-lived contact/serialization guards remain to protect stale UI references. Restart is required for reattachment.

## Persistence and linked missions

Supported persistent presentation and same-owner mission/occurrence references are automatically saved by the API. Consumers do not supply serializers or save/load hooks for these fields. After session/provider loss, consumers must explicitly place transient contributions again. State remains bounded and owner-scoped; refusal never means that older or unreadable state was accepted as empty.

Removal is a per-save persisted absence, not deletion: the retained row keeps its presentation so `Restore()` cannot invent defaults. Transient declarations keep their absence for the process session only.

A linked contact declares only its same-owner story definition (`mission:`); consumers never supply occurrence GUIDs. At placement the API resolves the unique currently admitted occurrence of that definition itself; while none (or more than one) is admitted, the patron reports `MissionNotReady` and stays `Waiting`, and automatic placement retries when readiness changes. A linked contact requires a current live story provider, registered definition, valid story admission and healthy runtime. Tentative registration and in-flight story operations cannot admit it. Registration, admission and operation epochs invalidate stale plans, including changes that return to an apparently identical state. Missing dependencies fail closed; consumers must not replace readiness tokens with constants.

Fail-closed is station-wide by design: while any admitted saved contact's linked occurrence is not
ready, the whole station's owned contributions are withheld (vanilla is retained) rather than
publishing a roster that silently omits one provider's contact. A completed or retired linked
mission therefore keeps its contact — and the shared station — withheld until the owning provider
removes the contact (`patron.Remove()`) or re-registers it without the association. Owners of
mission-linked contacts should remove them when their mission retires.

Native bar JSON contains only vanilla patrons and native metadata. Persistent owned contacts are reconstructed from API state after restoration and registration, preventing native factories from being asked to deserialize custom CLR types.
