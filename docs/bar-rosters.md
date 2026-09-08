# Owned bar rosters

Requires API 0.1.32. Enable `[Persistence] Enabled = true` and `[Bars] Enabled = true`. Bars are opt-in; check `ModApi.Bars` and the `owned-bars` capability rather than assuming availability. `RuntimeQualified` remains false: bounded native core checks do not establish complete consumer-combination acceptance.

## Provider lifecycle

Acquire a provider from the loaded BepInEx plugin instance in `Start` with `ModApi.Bars.AcquireProvider(this)`. Chainloader publishes its authenticated instance only after `Awake` returns; acquiring during `Awake` is refused. Consumers can latch managed mode in `Awake` to prevent a native fallback before acquisition. The API authenticates its stable owner identity. Register a `BarPatronDefinition` containing a local ID, native station GUID, display name, description and seed, with an optional interaction callback. Different providers may reuse local IDs.

`Place(currentSession.Id, localId)` stores the contribution. A successful placement is **not a visibility guarantee**: station policy, native capacity, readiness and dependencies determine admission at refresh. Always pass the current session identity; a saved occurrence ID does not make a stale session valid.

`Unregister` revokes runtime behavior and transient placement, retaining persistent state. Registering the same identity again can reconstruct that state without another Place call. `Remove` explicitly deletes placement state; in a ready session it is idempotent. Disposing the provider removes its runtime registrations, not its saved persistent rows.

## Station policy and capacity

`ConfigureStation(stationGuid, BarRosterOwnership.Additive)` contributes without evicting vanilla patrons. Native presentation has five seats; insufficient free capacity can deny contributions.

Exclusive ownership requires the exact plugin ID in `[Bars] ExclusiveProviders` (comma-separated). An authorized exclusive provider suppresses vanilla and other providers at that station; it does not delete the retained vanilla roster. Conflicting authorized exclusive claims deny every contribution and retain vanilla rather than selecting the last Harmony patch. Permission revocation is rechecked before application and interaction.

Policies are runtime registrations. They do not silently follow a provider that has been removed. The finalized observation reports denied providers and reasons. Providers should not bypass a refusal by mutating the native roster themselves.

## Refresh, observation and interaction

The API owns the sequence: native refresh, contribution admission/application, then finalized observation. `Subscribe(owner, callback)` returns a disposable subscription with no replay. Its immutable snapshot identifies the session, station, seats, seeds, native kinds, owned IDs and denied providers. A cache observer must use this final membership, not an intermediate native initialization list. Subscriber failures are isolated and diagnosed.

Interactions are dispatched to the current owner's callback only while the contact, session, policy and dependencies remain valid. Narrative dialogue, mission authoring and voice synthesis remain consumer responsibilities. The shared service does not persist callbacks, audio, UI objects or native handles.

Unknown modifications to a retained native roster are refused rather than silently losing suppressed vanilla patrons. On shutdown the adapter restores tracked native rosters when safe; process-lived contact/serialization guards remain to protect stale UI references. Restart is required for reattachment.

## Persistence and linked missions

Supported persistent presentation and same-owner mission/occurrence references are automatically saved by the API. Consumers do not supply serializers or save/load hooks for these fields. After session/provider loss, consumers must explicitly place transient contributions again. State remains bounded and owner-scoped; refusal never means that older or unreadable state was accepted as empty.

A linked contact requires a current live story provider, registered definition, matching unresolved occurrence, valid story admission and healthy runtime. Tentative registration and in-flight story operations cannot admit it. Registration, admission and operation epochs invalidate stale plans, including changes that return to an apparently identical state. Missing dependencies fail closed; consumers must not replace readiness tokens with constants.

Native bar JSON contains only vanilla patrons and native metadata. Persistent owned contacts are reconstructed from API state after restoration and registration, preventing native factories from being asked to deserialize custom CLR types.

## Verification scope

Controlled native checks cover independent owners sharing a local ID, repeated check-update calls, real bar UI openings, owned interaction dispatch, native JSON preservation, exclusive denial/conflicts, same-process reload and runtime provider removal/re-registration. A planned three-process sequence also checks saving while providers are unregistered and reconstructing from the fresh committed generation without Place calls; author assemblies remain installed in that absence phase.

Controlled composition checks cover the real Foundation builder, Anima offer finalization, explicit denial/admission on permission changes, forced native refresh and TTS's finalized object membership. Speech is disabled; the temporarily selected Foundation context does not establish natural travel, campaign progression or Foundation UI acceptance. See [consumer scope](../tools/bar-consumer-scope.md).

A same-process linked-story check covers an active API mission and persistent contact, automatic reload, unavailable-provider refusal with exact vanilla restoration, registration before reload and save-as rollback without replacement placement. See [linked scope](../tools/bar-linked-scope.md).

Missing author binaries, consumer-authored dialogue, voice/cache execution, natural daily-time transitions and API-created world-reference reconstruction remain outside that bounded evidence. See [probe scope](../tools/bar-probe-scope.md) and [independent authors](../examples/OwnedBarAuthor/README.md).
