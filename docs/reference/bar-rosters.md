# Owned bar rosters

Enable `[Bars] Enabled = true`; API-managed saves initialize automatically. Bars are opt-in; `ModApi.Services.Bars` is a stable `IBarService`; inspect typed `Availability` or observe `AvailabilityChanged`. The unavailable instance does not register a replacement save owner.

## Provider lifecycle

Acquire a provider from the loaded BepInEx plugin instance in `Start` with `ModApi.Services.Bars.AcquireProvider(this)`. Chainloader publishes its authenticated instance only after `Awake` returns; acquiring during `Awake` is refused. Consumers can latch managed mode in `Awake` to prevent a native fallback before acquisition. The API authenticates its stable owner identity. Register a `BarPatronDefinition` containing a local ID, native station GUID, display name, description and seed, with an optional interaction callback. Different providers may reuse local IDs.

Use `portrait: CharacterPortrait.Named("M2Captain")` to request specific NPC portrait art, or
`CharacterPortrait.OfCharacter("LuminateCommander")` to borrow a game character's portrait.
The seed still controls independent native seating/body presentation; it is not a portrait selector.
Omitting the portrait retains the default contact icon. An unresolved explicit portrait leaves the
contact usable without an icon and reports the missing identity once. Rebuilt contacts resolve it
again, so an initially unavailable art catalog is not cached as a permanent failure.

Portrait identities are saved with persistent patrons, not Unity objects. Schema-1 patron data
remains readable with its default portrait; schema-2 writes preserve the existing provider and
payload limits, including portrait bytes.

`Place(currentSession.Id, localId)` stores the contribution. A successful placement is **not a visibility guarantee**: station policy, native capacity, readiness and dependencies determine admission at refresh. Always pass the current session identity; a saved occurrence ID does not make a stale session valid.

`Unregister` revokes runtime behavior and transient placement, retaining persistent state. Registering the same identity again can reconstruct that state without another Place call. `Remove` explicitly deletes placement state; in a ready session it is idempotent. Disposing the provider removes its runtime registrations, not its saved persistent rows.

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

A linked contact requires a current live story provider, registered definition, matching unresolved occurrence, valid story admission and healthy runtime. Tentative registration and in-flight story operations cannot admit it. Registration, admission and operation epochs invalidate stale plans, including changes that return to an apparently identical state. Missing dependencies fail closed; consumers must not replace readiness tokens with constants.

Native bar JSON contains only vanilla patrons and native metadata. Persistent owned contacts are reconstructed from API state after restoration and registration, preventing native factories from being asked to deserialize custom CLR types.
