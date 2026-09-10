# Owned story content

Register authored definitions once through `ModApi.Services.Story`. Offer and control live
missions through their game and mission objects. The API owns occurrence identity, persistence,
restoration, safe execution and native ownership checks. All authoring members are main-thread-only.

## Authoring and reactions

The whole happy path in one block — acquire, define, register, react. `AcquireProvider` and
`Register` return nullable results so refusals stay honest; unwrap each once with its diagnostic:

```csharp
var introDefinition = new StoryMissionDefinition("intro", "First contact", "Meet the guide",
    new StoryFactionId("TradingGuild"),
    new[] { new StoryStep("Reach the vault", new[]
    {
        // WithKey links the declared objective to GetObjective(key); without it there is no keyed handle.
        StoryObjective.TravelTo("vault-poi").WithKey("reach-vault"),
        StoryObjective.Scripted("greeting", "Answer the guide", 1)
    }) },
    new[] { StoryReward.Credits(100) });

var acquired = ModApi.Services.Story.AcquireProvider(this);
var provider = acquired.Provider ?? throw new InvalidOperationException(acquired.Diagnostic);
var registered = provider.Register(introDefinition);
var intro = registered.Definition ?? throw new InvalidOperationException(registered.Diagnostic);
var nextResult = provider.Register(nextDefinition);
var next = nextResult.Definition ?? throw new InvalidOperationException(nextResult.Diagnostic);

var observing = new HashSet<IStoryMission>();
intro.Accepted += mission =>
{
    mission.GetObjective("greeting").SetProgress(1); // Absolute progress; safe to repeat.
    // GetObjective returns the same object per mission, and native retry can fire Accepted again
    // for the same mission - subscribe Changed idempotently rather than stacking handlers.
    if (observing.Add(mission))
        mission.GetObjective("reach-vault").Changed +=
            objective => Logger.LogInfo($"Vault progress: {objective.Snapshot.Progress}");
};
intro.Completed += mission => mission.Game.Story.Offer(next);
```

The `greeting` key must be a scripted objective declared by `introDefinition`. Definitions use
`StoryMissionDefinition`, `StoryStep`, `StoryObjective` and `StoryReward`; the supported subset is
listed below. Keep the provider until plugin teardown, then dispose it. Disposing a definition
removes only that registration and its subscriptions, not saved occurrences.

`IStoryDefinition` exposes `Accepted`, `Completed`, `Failed`, `Abandoned` and `Changed`.
Subscriptions survive game reloads. Their argument is the live `IStoryMission` for the game that
produced the event; use `mission.Game`, not an earlier game captured at plugin startup.

Gameplay handlers run at the API's safe delivery boundary, not inside the native mutation or save
callback. Exceptions are isolated per handler. Removing a handler or disposing its definition
suppresses pending delivery. Old-game deliveries are discarded, and restoration does not replay
historical acceptance or completion events.

If these actions and reactions depend on additional mod-owned save data, establish that dependency
once:

```csharp
var acquired = ModApi.Services.Story.AcquireProvider(this, saveData: mySaveRegistration);
```

Both story persistence and that registration must permit mutation before actions or reactions run.
There are no per-action session tokens, save gates or consumer frame drains.

## Live missions

From a `Game.Started` handler or another game-bound event:

```csharp
var mission = game.Story.Offer(intro);
mission.Activate();
```

`Offer` returns the mission immediately in `Offering` state. The API executes the offer safely;
`Offered` means the occurrence was admitted, and `Active` means the game accepted it. Every call
requests a separate occurrence. Pending offers are included in discovery, but are not persisted
until admitted. `Id` is empty until admission; callers do not need it to perform actions.

| Member | Meaning |
|---|---|
| `Activate()` | Ask the game to accept an offered mission, retaining its own mission-limit checks. |
| `Withdraw()` | Remove an offer that has not been accepted. |
| `Fail(choices)` / `Abandon(choices)` | End the mission natively before recording the requested outcome. |
| `DeclareChoices(choices)` | Preserve declared campaign choices for the eventual native outcome. |
| `GetObjective(key)` | Address a keyed objective within this mission. |
| `Changed` | A mission-state change or completed API action, including a refused action. |
| `LastAction` | The most recently reported action result. |
| `Choices` | Read-only declared choices, retained with the eventual campaign outcome. |

Action methods return a `StoryActionResult`. Its status starts as `Queued` and changes to
`Succeeded`, `Rejected`, `Unavailable` or `GameEnded`. Each returned result retains its own outcome,
even if several actions are queued. `Detail` explains a refusal. No retry driver is required;
`Changed` and the domain events provide notifications. Pending actions end when their game or
registration ends, without delivering callbacks into the replacement game. Completed action results
remain completed.

A mission object never rebinds after loading another game, even when the save restores the same
occurrence ID. Its state becomes `GameEnded`, and its actions cannot affect the replacement game.
A disposed or superseded definition likewise cannot control content through an old mission object.

Completion belongs to the game: there is no author-facing `Complete()` command. An observed failure
may still be retryable in the native UI. `Failed` therefore describes a failure, not necessarily a
terminal retirement; native retry can lead to another `Accepted` event. Neutral removal alone never
invents a completion, failure or abandonment.

## Discovery and retention

```csharp
var query = game.Story.GetMissions(intro);
if (query.IsAvailable)
{
    foreach (var mission in query.Missions)
    {
        // Offered, active and retained outcomes all carry their own game and actions.
    }
}
```

An unavailable query is distinct from a known empty game. No occurrence IDs need to be stored in
mod save data merely to find and control missions after reload. Discovery returns new game-bound
objects for restored occurrences.

- `Temporary` retains live content and the newest 32 terminal tombstones. It is not durable campaign
  history and cannot declare campaign choices.
- `Campaign` retains authoritative outcomes and declared choices without pruning. Use it for
  campaign progression rather than relying on a temporary tombstone remaining present.
- Withdrawing an unaccepted offer leaves no tombstone.
- Removed definitions do not delete saved missions. Provider-required content still needs its owner;
  see [content safety](content-safety.md).

Limits refuse admission rather than dropping existing state:

| Limit | Value |
|---|---|
| Definitions | 256 |
| Bound providers | 32 |
| Occurrences per provider | 64 |
| Campaign occurrences per definition, including unresolved content | 48 |
| Global occurrences | 2048 |
| Persisted payload per provider, including reserved outcome space | 16,383 bytes |
| Global story payload | 512 KiB |
| Declared choice keys | 8 |
| Encoded bytes per key / value | 32 / 64 |
| Reserved choice bytes per occurrence | At most 1024 |

An offer reserves enough space for its declared outcome. Campaign history and inactive providers'
retained data are not pruned to admit another provider. Global capacity remains a backstop when a
save contains more historical provider namespaces than can be bound at once.

Choices are bounded, copied when submitted and revalidated against the occurrence before execution.
Changing the caller's dictionary afterwards cannot change the queued action. Undeclared keys,
oversized values and malformed collections are refused without corrupting retained state. Choice
capacity is fixed at offer time; changing startup declarations does not enlarge an older occurrence's
reservation.

## Objectives and revisions

Supported objectives are `TravelToPoi`, `CollectCredits`, `Scripted`, `DeliverItems`,
`MineItems`, `SalvageItems` and `KillEnemies`; supported rewards are `Credits`, `Experience` and
`Reputation`.

`StoryObjective.KillEnemies(requiredAmount, enemyFaction)` counts destroyed units of the named
existing faction through the game's own trigger — the identity is required because the game
serializes and renders it, and it is validated against the registry at registration exactly like
the source faction.

`StoryObjective.MineItems(itemTypeId, requiredAmount, targetPoiId)` and
`StoryObjective.SalvageItems(requiredAmount, targetPoiId, itemTypeId?)` are the game's own
gather-at-location objectives: the engine itself counts the ore mined or material salvaged at the
target POI. A null salvage item means any salvaged material there counts (the native meaning); a
mining item identity is required and validated. Target POIs follow travel-target semantics —
authored sites from the world service are valid targets, so an exact-count mining field pairs with
an exactly balanced ore requirement. Progress is observable through `Snapshot`/`Changed`; the API
never counts gameplay itself.

`StoryObjective.TravelTo(targetPoiId, requireNewVisit?)` completes on the game's own visit record.
The native mechanism is a timestamp floor, not dwell time: `requireNewVisit: true` captures the
target's current visit record when the mission is built, so a player already sitting there must
leave and come back; the default accepts any recorded visit. `StoryObjective.ReturnToSource()`
targets the mission's own SOURCE location — wherever the occurrence is actually built/accepted,
which the game records natively — resolved per occurrence at build time. A campaign can therefore
begin and end at a vanilla questgiver location without ever knowing a generated guid, and the
return leg defaults to requiring a fresh visit. Return targets are exempt from registration-time
target validation (the identity does not exist until the occurrence is built).

`StoryObjective.TravelToAuthoredSystemEntrance(systemLocalId, occurrenceKey, requireNewVisit?)` and
`StoryObjective.TravelToAuthoredSite(siteLocalId, occurrenceKey, requireNewVisit?)` send the player
to content the API owns. The definition names the AUTHOR-LOCAL identities — which exist before any
session, in the same registry as the declaration — and the API resolves the native destination per
occurrence when the mission is built. While the provider's authored occurrence is not reconstructed
in the loaded game, the mission is neither offered nor accepted: refusal happens at the edge, never
a mission holding an unreachable step. If the world loses the destination AFTER the mission is
built, the objective query reports `DestinationLost` with `Knowledge = Known` — the API reports the
broken destination and decides nothing; the owner chooses whether the arc fails, the world is
repaired, or the player is told in the mod's own terms. One edge is deliberately stricter: if a
dissolved occurrence is RE-CREATED under the same author keys, it owns a NEW native destination,
and a mission built against the old one reads as unverifiable rather than lost — the API never
silently re-points a held objective at a different place than the one it was built for.

The native base objective carries an optional persisted `targetLayer`, but the gathering kinds do
not consult it when counting (it only informs the ship-capability/HUD hint), so this subset omits
it **deliberately** — a layer-restricted gather at a mixed-ore field would be a new need, not a
missing switch.

`StoryObjective.DeliverItems(itemTypeId, requiredAmount, deliverToPoiId)` is the game's own
item-delivery step: the native objective tracks the count at the delivery station and **consumes
the delivered items on mission turn-in** — the API reproduces none of that, it installs the real
native mechanism. Both identities are exact: an unknown item type or a delivery target without the
native turn-in shape (only stations qualify) refuses the offer rather than substituting, with the
same missing-dependency semantics as travel targets. `Snapshot`/`Changed` observe the native count.

Rewards are declared through factories only — one idiom per kind, and a kind can never carry a
parameter it does not support:

```csharp
new[]
{
    StoryReward.Credits(120000),
    StoryReward.Experience(2000),
    StoryReward.Reputation(600),                       // the mission's source faction
    StoryReward.Reputation(50, new StoryFactionId("MiningGuild"))
}
```

### Completion, narrative endings and step tracking

Completion is game-owned: by default a fully satisfied mission stays **claimable** until the player
turns it in through the mission log. A mission that ends on a narrative beat declares
`.WithAutoComplete()` and authors a final `Scripted` objective that the closing conversation
satisfies via `SetProgress` — the game then completes the mission itself at its native safe point,
which explicitly waits for any open dialogue to finish first, so the closing beat can never race
the archive. There is no consumer-driven `Complete` call.

There is deliberately no current-step index: steps reorder across content revisions and indices
break saves, so beats gate on **objective keys** (`GetObjective(key).Snapshot` / `.Changed`). A
"live step" is derivable when genuinely needed — the first step whose keyed objectives are not all
complete — but key-gated beats do not need it.

### Native surfaces and the offer marker

`mission.NativeMissionId` is the identifier native surfaces accept for that occurrence — most
importantly character `missionHighlights` for the pickup marker. It is null until the offer is
admitted. The native marker shows while a highlighted identifier is offerable and not held, so the
flow is: evaluate your own prerequisite, `Offer`, then highlight `mission.NativeMissionId`; the
player picks the mission up through the character's native dialogue, and verified acceptance fires
`Accepted`. Never highlight the definition-level `NativeIdentifier` — it is a catalog placeholder
that is never offerable, so its marker would show forever and its pickup is refused.

`StoryReward.Reputation(amount)` grants reputation with the mission's **source faction** (the
native default); `StoryReward.Reputation(amount, faction)` names another existing faction,
validated against the game's registry at registration exactly like the source faction. Reputation
may repeat per distinct faction; other reward kinds stay unique. Item rewards remain outside the
subset.

```csharp
var objective = mission.GetObjective("answer");
objective.SetProgress(3); // Absolute scripted progress, not an increment.
var snapshot = objective.Snapshot;
```

Declare a scripted objective with `StoryObjective.Scripted(key, description, requiredAmount)`.
Progress is monotonic within an attempt; repeating a value does not increment it. Inactive steps,
unknown keys, changed native objects and writes to game-owned objective types are refused. Native
retry resets progress for the same occurrence. Setting progress does not directly grant rewards.

`objective.Changed += changed => ...` observes progress changes as a safe gameplay reaction,
including native trigger-driven progression of game-owned objective kinds — no consumer polling
or frame driver. Changes are observed relative to when the objective object was obtained; the
event delivers the live objective, so `changed.Snapshot`, `changed.Mission` and actions are
directly available. Delivery follows the same rules as mission events: only while the game is
active and the definition registered.

`Snapshot` distinguishes unavailable progress from zero. For keyed native objectives, credits are
current nonnegative balance capped at the requirement, and travel is native completion, not dwell
time. Offered or retired native objectives have no live progress answer. The API re-resolves the
held mission and verifies its shape instead of retaining a native objective across reloads.

`WithRevision(newRevision, migratesFromRevision)` permits a specific revision migration. Supported
migrations use fully keyed scripted definitions: preserve old keys and required amounts, reorder
steps if needed, and start added keys at zero. Missing keys, changed kinds or amounts, unapproved
revisions and insufficient capacity are refused. Active migration also requires unchanged non-step
metadata, including rewards and choices. Mixed and unkeyed definitions do not acquire invented
migration identities.

## Identity, persistence and native safety

Call `AcquireProvider(this)` directly from the plugin's own assembly. The host authenticates the
plugin instance against the calling assembly and derives its provider namespace. A different loaded
plugin cannot acquire that identity merely by passing its instance. This is ordinary mod-to-mod
ownership, not a sandbox against reflection or arbitrary in-process code.

A second live lease for the same plugin is refused. Local identifiers are 1–48 lowercase ASCII
letters, digits or hyphens, starting with a letter. Duplicate local registration, catalog collisions,
unsupported definitions and exhausted limits produce explicit registration refusals. No existing
catalog entry is replaced accidentally. Disposing an old handle cannot remove a newer registration.

The API owns a bounded schema-4 story payload. Schemas 1–3 remain readable through internal save
migrations. Newly offered occurrences retain immutable definition data, so changed startup text,
targets or rewards do not replace saved generated content at the same revision. Definition payloads
are released at retirement; outcomes, choices and keyed progress follow the retention policy.
Strict encoding and shared ledger/codec bounds reject malformed state rather than truncating it.
Restoration replaces the ledger with the exact loaded generation; newer outcomes cannot leak into
an older save. Corrupt or unsupported owner data is preserved, not overwritten with empty state.

Vanilla saves accepted mission instances. The API retains what vanilla does not: offers, occurrence
identity, outcomes and choices. Each occurrence has its own native catalog entry because vanilla
refuses duplicate active or archived story identifiers. Source faction is mandatory because vanilla
serializes it unconditionally; use exact native faction type identities such as `TradingGuild`.
Travel targets are checked at admission and acceptance, and the game's own acceptance and mission
limit rules remain authoritative.

Native acceptance and ending are revalidated after callbacks. An invalidated acceptance is undone
without archiving it; an unaccountable native change blocks further story mutations rather than
building on inconsistent state. Native retry remains one protected transaction. Missing providers,
unreadable worlds and unclaimed owned missions suspend progression without adopting, deleting or
inventing outcomes for those missions.

## Availability and protection

Story authoring requires `Story/Enabled`, inspected bindings, API-managed saves, native protection
and observed mission transitions. `ModApi.Services.Story.Availability` and `AvailabilityChanged`
report service health. Unknown save state remains distinct from an empty game.

The independent `story-protection` capability (`Story/Protection`, default on) prevents unadmitted
owned missions from advancing, receiving triggers, completing or paying rewards. It also protects
the native abandon/retry route. Ownership is resolved against the current player, with bounded
scans; inability to determine ownership fails closed and reports degradation. Unrelated, positively
identified non-owned missions are unaffected. Quarantined content remains in the save unchanged.

**Protection requires an inspected game build and enabled, bound hooks.** With an unsupported build
or `Story/Protection=false`, the API refuses new authoring but cannot stop already saved owned
missions from running. Do not load an owned-content save without its required protection; keep a
backup. Narrative choreography, extra mechanics and voice synthesis remain the mod's responsibility.
