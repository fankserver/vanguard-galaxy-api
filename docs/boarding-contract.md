# Boarding integration constraints and source coverage

Optional boarding observation is implemented in API 0.1.25, disabled by default and not runtime-qualified. Enable `[Boarding] Enabled = true`, inspect the `boarding-observation` capability and use `ModApi.Boarding`. API 0.1.26 also exposes `ModApi.BoardingRules` with the independent `boarding-rules` capability. API 0.1.27 exposes `ModApi.BoardingCommands` when `boarding-commands` is available. API 0.1.28 exposes `ModApi.BoardingTactics` and `ModApi.BoardingCombat` under the separate `boarding-tactics` and `boarding-combat` capabilities. Authored content and presentation registration are not available yet. This document distinguishes the observation contract from applicable constraints on those integrations; no native boarding scenario is attested by it. Delivery and remaining acceptance belong to [milestone 09](https://github.com/fankserver/vanguard-galaxy-api/milestone/9), not a second source-tree backlog.

## Evidence boundary

Member mappings apply to the original `Assembly-CSharp.dll` SHA-256 `a2aad60bc68c31baccd636587d3c5ba4e651eacda59b0af42cd4f17f864284fb`. They are semantic source evidence, not Unity qualification. Original source remains private; this document contains findings only. Unknown hashes cannot enable integration by matching names/signatures alone. Consult [compatibility](compatibility.md) and [lifecycle](lifecycle-contract.md) for threading, patch-group rollback and readiness constraints.

**Boarding is an encounter lifecycle, not an always-boardable flag.** Ship disabling, crew transport, interior simulation, UI and settlement are separate boundaries. Ship boarding and walk-in installations share `DungeonSimulation`; shared rules require explicit `Ship`, `Installation` or `Both` scope. A station victory is not ship capture. API naming uses *boarding* for the service and *encounter* for shared interior state; public identifiers must not expose vanilla `DungeonType` or Unity objects.

## Functional coverage matrix

Each row identifies the implementation delivery issue and native evidence required. A mapped issue is not evidence of implemented coverage. Observation for every row belongs to #111, save continuity to #117, and applicable native qualification to #120.

| Function | Inspected members / semantic boundary | Contract and delivery | Required native evidence |
|---|---|---|---|
| Structural eligibility | `SpaceShip.CanBecomeBoardable`; excludes player, no-board flag, carrier fighter, drone, IndustryStation, level >80 and predefined loot | Reasoned query; retain exclusions unless an independently inspected explicit override exists (#112) | Each exclusion, allowed target and lethal hit |
| Disable policy | `AbstractUnit` damage path → `SpaceShip.HandleBoardingCheck` → `BecameBoardable`; conversion invokes death/removal and replaces the world object | Evaluate at original damage boundary, preserve damage context; not a bool setter (#112) | Threshold, EMP/RNG vanilla pass, zero hull and single conversion |
| Target lifetime / re-engage | `BoardableUnit.Start`, click/recovery paths; restored simulation/pods, idle re-engagement, repaired departure | Target generation and availability, live panel action; no re-engagement during operation (#111, #113, #118) | Replacement, unload, recovery, stale target |
| Start / reputation | `DungeonManager.StartOperation`; `DungeonPanel` builds options; the misleading `BoardableUnit.StartBoarding` is not the creator | Validated start with travel/crew/target checks; preserve faction consequences (#113) | Ship and installation entry, transponder exceptions, duplicate start |
| Crew / pod transport | `DungeonOperation` construction/landing/reinforcement/return; manager reconstructs pods | Debit before transporting; native reinforcement transport alone does not debit (#113) | Launch/cancel/land/reinforce/return and loss |
| Autonomous behavior | `AutoDungeonDirector.Tick`: retreat on no living friendlies or average morale <0.15; sustained locked-route blockage after grace period | Same validated policy boundaries; controller arbitration, no panel requirement (#113, #114) | Manual/autonomous switching, no-specialist blockage, panel closed |
| Enemy reinforcement | `BoardingReinforcementActions` approaches an existing boardable target, invokes supplied crew callback in range and resumes prior ship actions | Distinct defender arrival, not proof of boarding the player's ship (#113, #114) | Target disappearance, hostile arrival, restored pods |
| Definition setup | `DungeonDefinition.LoadAll/Get`; resource-loaded dictionary keyed by native enum; `ApplyDefinition`, `ApplyCombatMode` | Provider/local definition registry separate from native enum; validated attachment/selection (#115) | Two providers, registration reset, creation and reload |
| Layout and occupants | `DungeonCompartmentLayout`, `InitBoardingLayout`, `PlaceDefenders`, `PlaceAttackers`; ship-size capacities and staging | Validated compartment graph/layout and existing crew catalog references (#115) | Invalid graph, airlock, capacity, saved layout |
| Difficulty / estimates | `ApplyLevelScaling`, `EstimateFromData`, creation before defender placement | Creation-only power/HP tuning; UI estimate and live values agree (#112) | Easier/harder, first placement, resume without reapplication |
| Combat / armor / ammo | `TickCompartmentCombat`, `ApplyCombatDamage`, `ApplyCasualties`, ammo/armor modifiers | Bounded synchronous policy proposals, immutable facts, no mutable simulation callback (#114) | Casualties, armor, ammo integrity, competing modifiers |
| Morale / capture / defection | `TickCompartmentCombatMorale`, surrender/defection and panicked movement; `FactionBoardingProfile` | Per-encounter copied profile/configuration, not mutation of shared cached faction profiles (#114) | Surrender, no-surrender faction, defection, wounded/dead |
| Exploration / specialists | Investigation, profession events, `DungeonFacilityEvents`, discovery/combat/interaction/lockdown event dispatch | Discovered information only by default; authored event definitions and typed actions (#114, #115) | Discovery, specialist arrival, missed event, restore |
| Movement / capacity | `IssueMovementOrder`, `ClearPlayerMovementOrders`, `MoveCrewTo`, directive routing | Validated compartment/unit handles and movement requests, phase/capacity/filter checks (#114) | Locked path, busy specialist, full room, stale directive |
| Unlock / barricade | `TryUnlockCompartment`, `CanStartBarricade`, `ToggleBarricade` and timers | Typed eligibility/result, scoped action notifications (#114) | Missing specialist, contested room, lock timer and reload |
| Grenades / hazards / venting | `CanThrowGrenade`, `ThrowGrenade`, hazard damage and airlock vent methods | Cooldown/resource/reachability checks; distinguish tactical player action from faction automatic behavior (#114) | Friendly damage, cooldown, inaccessible room, vent deaths |
| Buyout / reinforcements | `AcceptBuyOut`, `DeclineBuyOut`, reinforcement schedule | Validate pending choice and costs, preserve inventory/crew accounting (#113, #114) | Insufficient credits, repeated choice, delayed arrival |
| Scuttle / collapse / destruction | `TryScuttle` → `FireScuttleOutcome`; ammo damage differs from reactor explosion; `DamageFacility`, `NotifyHostDestroyed` | Modify damage before application with a cause; separate scuttle permission from damage scaling (#112, #114) | Ammo vs reactor, zero multiplier, host death, nested evaluation |
| Retreat / extraction | `TriggerRetreat`, `RequestExtraction`, `ConfirmExtraction`, operation completion handlers | Distinct request, resolving and terminal states; do not promise immediate return (#113, #116) | Voluntary retreat, forced retreat, investigating victory, extraction |
| Capture / rewards / missions | `BoardableUnit` capture/outcome and `DungeonOperation` loot/crew settlement | Capture is native multi-step mutation; report actual delivery, mission-token exceptions and later crew return (#116) | Normal/token capture, partial loot, overflow, prisoners, mission effects |
| Presentation | `DungeonPanel`, compartment/movement controls, boarding status/cancel HUD, results panel | Separate live panel capability; provider-scoped text/actions with activation-time validation (#118) | Keyboard/mouse, resizing, target death, close/reopen, two contributors |
| Save / reconstruction | `DungeonLocationData`, `DungeonData`, `DungeonSimulation` serializers; pod data and player captured ships | Reuse native fields only where verified, supplement API-owned progress automatically (#117) | Every transport/encounter/settlement phase and save failure/rollback |

The matrix groups mechanics, not public methods one-for-one. An action family's helper algorithms need focused reinspection when an implementation intercepts them; the matrix is not a claim that every tactical branch has been exercised. Adding a raw reflection escape hatch does not satisfy any row.

## Inspected command and controller semantics

The supported vanilla ship entry path excludes `IsPlayer()`. Enemy reinforcement pods land on the existing boardable target and add defenders; they do not attack the player's ship. Symmetric player-defence boarding is therefore **not a supported vanilla entry path**. Supporting a newly invented player-defence system requires separately approved gameplay scope, not bypassing that exclusion. This finding does not assert that arbitrary external calls are impossible.

`DungeonOperation.isAutonomous` enables the supervisory director and automatic extraction. `DungeonOptions.autoMove` independently selects attacker routing in the simulation; disabling autonomy does not stop automatic movement. The director handles retreat, while both manual and automatic movement paths process directives. Neither a queued directive nor an eligible-unit count reserves crew or proves arrival.

API commands must add validation absent from native helpers:

| Native action | Important prerequisite / result limitation |
|---|---|
| `ThrowGrenade` | Checks mode, completion, charges, cooldown, target/reachability and hostiles; consumes a charge and damages both sides. Cooldown is 10 seconds on the inspected build. |
| `ToggleBarricade` | Starting checks mode, completion and Friendly room; cancellation lacks those common guards. Toggle return is not construction completion. |
| `IssueMovementOrder` | Indexes without bounds checks and clears old orders; a locked target emits one specialist directive irrespective of requested count/filter. |
| `MoveCrewTo` | Can return true with zero moved units. Verify membership, liveness, phase, source indices, adjacency, transit and capacity; native lock-clearing is not proof a specialist arrived. |
| `TryUnlockCompartment` | The helper does not establish living friendly membership, adjacency or operation readiness; starts a timer rather than instantly unlocking. |
| `AcceptBuyOut` | Can debit credits and clear the pending choice before discovering there is no candidate and returning false. Validate the candidate before invoking; never interpret false as proof of no mutation. |
| `ConfirmExtraction` | Its weak retreat guard is not permission to extract from any phase. Validate the pending extraction state independently. |

Enemy donor selection consumes a reinforcement request before finding a donor, debits donor crew before dispatch and restores previous movement actions on completion/abort. Missing-target abort is not a proven crew refund. Commands and settlement observations must account for these boundaries instead of fabricating successful delivery.

## Public shape and identity constraints

The current observation surface is `IBoardingEvents`, `BoardingHandle`, `BoardingTargetSnapshot`, `BoardingOperationSnapshot`, `BoardingCompartmentSnapshot` and `BoardingEvent`. `BoardingHandle` is opaque runtime identity; separate query dictionaries distinguish targets from operations. Registration is main-thread-only, does not replay, and is disposed through the returned subscription. All snapshots copy their collections. Invalidated/retired handles cannot be queried or resurrected.

The following naming and behavioral constraints apply to richer interfaces; names not listed above are design terminology, not advertised available types:

- `IBoardingApi`: queries/subscriptions plus optional command/rule/content/presentation interfaces. Each independently bound integration reports availability and an actionable reason. A live service or `GameplayInitialized` does not imply target, panel or command availability.
- `BoardingTargetHandle`: session ID and opaque generation; `BoardingOperationHandle`: session ID and opaque operation generation. Compartment and crew handles additionally belong to an operation. Constructors must not let arbitrary identifiers confer mutation authority.
- Immutable `BoardingTargetSnapshot` and `BoardingOperationSnapshot`: revision, encounter kind, phase, explicit availability reasons, copied crew/pod counts, discovered compartments, options, integrity and separately reported outcome/settlement. Collections are defensively copied. Unknown facts are unknown, not fabricated zeroes or success.
- Distinct operation phases: approach, awaiting landing, active encounter, extracting, resolved, returning crew, settled, retired. A target may exist without an operation. Phase transitions need native evidence; a closed panel changes no encounter phase.
- `BoardingCommandResult`: accepted, unavailable, stale handle, wrong phase, invalid argument/target, insufficient resources, existing operation, busy/dispatching or conflict. Acceptance means admitted only. Partial native failure must not be reported as a completed outcome.
- Provider/plugin ID plus local registration ID identifies definitions/rules/actions. Occurrences have separate save-bound identities. Namespacing prevents collisions, not hostile same-process access. A human's approval is not a property of a provider ID.

Capabilities must separate observations, operations, rules, authored content and presentation. A failure in an optional presentation group does not make safe observation unavailable. Within a group, exact binding/installation is all-or-nothing. No capability is runtime-qualified by a successful bind.

## Observation delivery

Target `Start` hooks and manager start/resume return boundaries identify live native instances. `EnsureApproachOperation` reconstruction is a resume boundary even when no simulation exists yet. Operation `Tick` and inspected settlement hooks supply changed snapshots independently of the panel. Starting an existing operation does not emit another start. Resume seeds victory/resolution state without replaying those as newly achieved facts. Known rooms exclude `Unknown` rooms; crew counts exclude killed, surrendered, captured or zero-HP units. Autonomy and auto-move are distinct fields. Target availability reports travel, no crew, installation integrity/level restrictions, active operations and destroyed targets; it is not a reservation or permission to execute a future command.

`VictorySecured`, `SimulationResolved`, `CaptureApplied` and `CrewReturnSettled` are separate facts. A nonthrowing operation completion is not return settlement. Direct crew return, docked-crew cancellation/recall or the final player-pod-return handler supplies settlement evidence. Remaining enemy reinforcement pods do not count as player-return obligations; mixed docked/launching cancellation waits for the launching player pods. Total active pod count remains a separate snapshot fact. Each observed player pod remains an outstanding obligation until its own return handler succeeds or an inspected docked-return boundary removes it. A pod disappearing, becoming non-live or being removed without such evidence leaves settlement unresolved; unload retires that operation without a successful crew-return event, including after a partial return.

A destroyed/unloaded target generation is retired independently of continuing crew-return tracking. `Retired` removes the target query; known operations can still emit immutable settlement facts referencing that former target without resurrecting it. Replacement ship components receive new target generations even when native location data is reused. Multiple installation parts share their target generation while at least one observed part remains live. `OperationRetired` removes a completed operation without pending player pods, or an unloaded operation with no live return obligation. It is not a success event. Late native signals cannot revive retired operation identities. Session invalidation clears all live queries; immutable event snapshots remain usable as records of their observed boundary.

`RewardsDelivered` includes one `BoardingDelivery` route/quantity receipt, **not a complete-batch success claim**. Only nested successful inventory applications, actual positive credit balance changes, or registered world loot produce receipts inside boarding reward scopes. A transfer batch returning with no application emits none. World loot means a registered world drop, not cargo delivery. Data routed through native inventory additions is an inventory application. Special currency conversion returning no inventory entry has no inventory receipt; it must not be misreported as cargo. Detailed item identities and complete settlement accounting are governed by #116. Buffered successful inner applications remain facts if a later batch step throws; the original exception is preserved.

Observed snapshot changes receive monotonic session-local sequences. Foreign-thread/adapter faults stop observation and clear live state; subscriber faults alone do not stop it. Binding validates all snapshot members and hooks before enabling the optional group, including nested metadata type spelling. Host tests cover lifetime, reconstruction suppression, discovery, separate return settlement and reward-scope behavior; installed metadata checks cover shapes. Neither is Unity/Harmony qualification.

## Callback and conflict contract

Main-thread-only access follows the lifecycle service. Callbacks are short, synchronous and nonblocking. Observers cannot cancel or mutate the transition they observe. Before-action policy evaluation receives immutable input and returns a validated proposal. Native exceptions retain vanilla behavior; only provider failures are contained.

Rule/action registration order is explicit priority followed by ordinal provider/local ID, not plugin load timing. Observational subscriptions use registration order and have no policy priority. Evaluation snapshots registration identities at entry; additions apply next time, disposed-before-turn callbacks are skipped. Removal does not mutate an evaluation already returned to vanilla. Each registration lease is disposed at provider teardown.

Restrictions compose as additional denials. Numeric multipliers compose exactly once in deterministic order using checked finite arithmetic; per-field bounds are part of their typed contract, and out-of-range/NaN/infinite proposals are rejected rather than clamped secretly. A rejected provider proposal leaves the previous valid baseline unchanged and emits diagnostics. Conflicting exclusive overrides at equal highest priority do not silently choose a winner: use the native baseline with explicit conflict diagnostics. A required command validator failing rejects that command; it does not grant permission.

Commands refuse execution during policy evaluation, lifecycle dispatch, serialization and settlement. Consumers may retry on a later update with fresh handles; the API does not automatically replay stale commands. Revalidate session, generation, phase and resources after callbacks before applying changes. Native callbacks may invalidate the target. Do not reuse static prefix scratch fields for per-operation state.

Exclusive command control uses an owning lease, distinct from subscription or a provider string. Native/manual/autonomous controllers require an explicit arbitration policy; a second mod cannot silently take over. A disposed controller does not itself cancel native transport or lose assigned crew.

## Automatic save/load constraints

Creation-time tuning and live policy are different. Native saved level multipliers remain authoritative on restore; construction callbacks cannot run again merely because a simulation is reconstructed. Live policies use currently registered behavior on later actions. Shared faction profiles must not be mutated globally; per-encounter resolved values need a declared saved-field contract.

API-owned definitions, occurrences, supported resolved parameters, choices and settlement state follow the selected game save automatically. Mods re-register callbacks, not serializers, sidecars or reconstruction schedules. Additional unrelated custom mod data is optional. Required persistent creation refuses when its save support is unavailable. Explicitly transient presentation handles and observational snapshots are not persistent gameplay content.

### Native ownership graph and known gaps

`SaveGame.SaveCurrentState` obtains `GamePlayer.ToJson`, whose map traverses `GalaxyMapData` → sector → system → `MapPointOfInterest.WritePersistablesData` → `DungeonLocationData.DataToJson`. A location owns `DungeonData`/simulation, ship data and nested pod records. Ordinary registered boarding locations survive POI filtering unless tagged as excluded generated representations. The reverse factory chain reconstructs JSON data first; `BasePoiManager.InitializePoi` later instantiates world persistables and `BoardableUnit.Start` resumes operations/pods. JSON parsing is not world readiness.

The current vanilla serializer is insufficient for the complete API save contract:

- `DungeonManager.ReconstructSinglePod` handles Docked, Launching and Attached, not Returning/Arrived. Generic pod `AddToWorld` returns null. Returning survivor crew is private runtime state separate from the original serialized manifest; both manifest and recovery require API integration.
- Enemy reinforcement pods registered through the generic runtime path are not added to the location's saved pod list. Their in-flight donor action/callback is not restored; landed defenders can persist in the simulation.
- Resume/reconstruction resolves the current player ship rather than restoring an attacking-instance identity from `parentShipId`.
- Pending directives, operation phase/controller timers and runtime callbacks are not serialized continuation state. The API must explicitly save supported command progress or resolve it safely; it cannot promise exact native replay.
- Grenade cooldown and reinforcement schedule/request flags **are** serialized through event-state fields. Unlock and lockdown timers are serialized. Do not duplicate or incorrectly classify those as absent.
- Pending-extraction recovery is a separate direct crew-recovery path, not returning-pod reconstruction. It must not race with API recovery and duplicate survivors.

These gaps are delivery requirements of #117 and #120, not exemptions from automatic save/load. Returning manifests need stable occurrence/settlement identity; frame-derived pod names are not persistent uniqueness. Mutable transform data is copied by native update components, not a transactional save snapshot.

Native serialization is not an atomic transaction with API storage. Save-as, rollback, failed/skipped writes, absent providers, schema migrations and return-pod continuity require explicit recovery behavior. A session ID is not a campaign or occurrence key. `SaveStarted` is after vanilla snapshot construction and is not an authoring hook. See [development constraints](implementation-plan.md) and [save storage](persistence-storage.md).

## Consumer boundary

BoardAlways owns its Enabled setting, threshold/guaranteed-disable policy, balance multipliers and chosen ship/installation scope. The API owns safe conversion, native side effects, once-only modifiers and checked command accounting. Its difficulty code currently omits Enabled and rejects values outside the easier-only interval; its scuttle restoration can scale ammo damage twice and cannot undo explosions. Those are migration corrections, not API compatibility requirements.

Patch-free means no direct game/Unity/Harmony compile references, reflection or native casts for covered boarding functionality. A wrapper moving the same patches into another consumer file does not qualify. A second author example must exercise custom encounter/tactical/UI/save behavior beyond the four BoardAlways patch areas.

## Tactical actions and combat policies (API 0.1.28)

Tactical execution requires the actual current `IBoardingController` instance, not merely its plugin ID or an imitation of the interface. `BoardingTacticalRequest` describes an action; `BoardingTacticalSnapshot` copies discovered rooms, grenade charges/cooldown and extraction availability. Adjacent unexplored rooms expose only their index, unknown status and door state so exploration remains possible without revealing their occupants. Snapshots resolve the exact requested operation generation, never its target's newer operation. Snapshots are not permission and action execution revalidates native state. Native direct movement retains capacity-limited partial movement; queued API count requests require the requested capacity.

| Family | Implemented contract / native boundary | Validation evidence |
| --- | --- | --- |
| Movement and priority | Move, clear orders, retreat from a room, set/clear priority; native `IssueMovementOrder` and autonomous `MoveCrewTo` are checked | Invalid indices before order clearing; visibility, count/capacity, foreign/duplicate crew, transit and adjacency |
| Locks and specialists | Locked movement routes a specialist; Unlock starts the native timer with an available adjacent specialist | Actual supplied unlocker, liveness, membership, directive assignment and existing timer |
| Barricades | Toggle uses native start/held predicates | Active, visible, intact room and native prerequisites |
| Grenades | Throw uses native resource/reachability predicates; API requires explicit friendly-damage consent | Charges, cooldown, target and terminal state |
| Ammo, stealth/noise and auto-move | Controller SetOptions uses typed native options, keeping the native ammo/noise relationships | Enum validation, phase and control gates; no duplicate simulation escape hatch |
| Buyout and extraction | Accept/decline buyout; request/confirm extraction | Candidate exists before debit, credits, pending offer, victory and pending extraction |
| Power and initial HP | Combat multipliers run at effective-power and new HP initialization boundaries, scoped to a simulation | Per-side composition, scope nesting and vanilla fallback |
| Morale and casualties | Morale change magnitudes and casualty rate have separate multiplier families | Morale adjusted before combat surrender and attacker panic/recovery checks, not applied twice; finite nonnegative values |
| Surrender and defection | Veto combat/mass surrender, attacker morale collapse and combat/faction side switching | Discrete veto composition and session/reentrancy behavior |
| Reinforcements | Veto defender scheduling or player requests before debit, not already-arriving manifests | Player roster conservation on veto; receiving simulation remains mandatory |
| Hazards and venting | Veto native hazard firing, airlock vent attempts or random structural vent selection | Discrete family validation and exact native binding checks |
| Structural damage | `IBoardingRules.RegisterIntegrity`, scuttle and explosion policies | Cause-aware exactly-once composition; authoritative host destruction remains unchanged |

`IBoardingCombatRules.AcquireProvider` creates a disposable provider instance independent of command control. RegisterMultiplier accepts Power, InitialHealth, Morale or CasualtyRate; RegisterVeto accepts Surrender, Defection, Reinforcement, Hazard or Venting. Callbacks receive copied encounter kind/level, side, optional room and boundary value. InitialHealth scales the native HP initialization multiplier; Morale scales the absolute change, retaining its sign and clamping resulting morale to [0,1]. Policies do not rewrite saved HP on load.

Individual multipliers must be finite in [0,10]; combined multipliers above 100 or overflowing the boundary value reject the offending contribution with diagnostics. Contributions run by descending priority then ordinal provider/local ID. Vetoes aggregate as denials, and throwing callbacks do not prevent later contributions. Provider disposal removes only that instance's registrations. Nested evaluation preserves native defaults; session replacement discards results. Policy callbacks must not issue commands.

These are request/effect vetoes, not outcome notifications. Vetoing a hazard effect can leave native event scheduling/progression intact; refusing reinforcement scheduling delays the request rather than deleting arriving crew. Power, health, morale and casualty changes influence subsequent native RNG outcomes; vetoes skip the intercepted native draw, while vanilla numeric defaults add no API random draw. Native UI and autonomous tactical helpers share checked boundaries with API requests. An explicit native UI grenade action retains native friendly-damage semantics; API callers must opt in.

Host tests, source inspection and metadata checks are not Unity acceptance. Native timing, coexistence, presentation and complete action-family scenarios remain unqualified until exercised on the exact source revision.

## Boarding commands (API 0.1.27)

`ModApi.BoardingCommands.AcquireControl(pluginId, target, out controller)` returns a typed result and, when admitted, an instance-scoped disposable controller. Acquire from a current target snapshot, not a saved handle. Event subscriptions do not grant command control. Only one mod controller can hold a target; manual native HUD cancellation and panel start, extraction, reinforcement and option actions revoke it. Native autonomous re-enabling is blocked while it is held. Disposal does not restore old autonomous settings over newer player choices.

The controller exposes Start, Resume, Reinforce, CancelApproach, Retreat, RequestExtraction, ConfirmExtraction and SetOptions. Crew manifests are copied, nonempty, positive-count maps of native crew identifiers. Options expose ammunition, stealth, auto-move and automatic buyout. Automatic buyout can spend credits later according to native rules; no upfront credit charge is invented. Starting with friendly-faction consequences requires explicit consent, then uses native reputation/aggro bookkeeping. Availability, crew, capacity, travel, phase and target/ship identity are revalidated at execution. Installation entry restrictions do not apply to ship targets. Reinforcement requires an existing receiving simulation; approach or prelanding without one is rejected before crew debit.

Initial ship crew is debited at native pod creation. Walk-in crew is revalidated and debited at actual entry, not reserved during approach; an insufficient delayed roster cancels the operation without a partial debit. API-owned initial transfers and reinforcement use batch debit, with notification after the native transport boundary. A possibly dispatched native failure is never automatically refunded or retried. Exceptions propagate, including both transport and notification failures when both occur. Cancellation invokes the exact operation's native abandonment path, including pod recall, rather than selecting another operation belonging to the same ship. Native crew-return handling remains independent of panel visibility.

Commands refuse reentrancy, event/policy dispatch and native save-state serialization. Session/target replacement invalidates controllers. Admitted means the native request was entered, not arrival, extraction completion, successful rewards or settled crew. Observe lifecycle events for those separate facts. Host tests and installed binding checks do not prove the cancellation, return, UI or save paths in Unity; complete native acceptance remains pending.

## Registered boarding policies (API 0.1.26)

Acquire one disposable `IBoardingRuleProvider` per plugin ID from `ModApi.BoardingRules`. Each local registration ID is unique within that owning provider instance. Dispose a registration to remove it, or dispose the provider to remove all its rules. Registration and evaluation are main-thread-only. Check `boarding-rules` availability; binding success is not native qualification.

Callbacks receive immutable numeric contexts and must be pure, quick and deterministic. They must not call game commands. Reentrant evaluation uses vanilla defaults. Registration changes take effect on the next evaluation snapshot; disposed entries are skipped immediately. Session replacement during evaluation discards the result. Exceptions and invalid values discard that contribution and report the provider/local ID without suppressing vanilla exceptions.

- Disable rules return Vanilla, Allow or Deny at the original structurally eligible damage boundary. Every Deny takes precedence over Allow. A threshold is expressed using HullFraction. Zero hull is valid; the player, quest/predefined-loot ships and all native structural exclusions remain excluded. Conversion uses the original damage object and native death/removal routine exactly once; a native exception propagates, never retries conversion.
- Chance registrations return a probability in [0,1], or null for vanilla. They are considered only when disable rules all pass. Highest priority wins; different proposals tied at that priority preserve vanilla. A chosen probability replaces the native roll for that damage boundary, not its accumulator formula. Zero/one consume no random draw; intermediate probabilities draw once. Vanilla pass consumes no API random draw and runs the original accumulator/EMP/autopilot logic.
- Encounter tuning multiplies defender power and health only during new native simulation creation, before defender placement. Both easier and harder settings are supported. Saved native multipliers remain authoritative and are not reapplied on reconstruction. Changes to rules do not retune existing simulations. Native pre-entry estimates incorporate both power and health multipliers into the existing difficulty heuristic. Zero health produces zero estimated defender power; these remain heuristics, with rounding and individual crew composition limiting prediction accuracy.
- Integrity policies return an individual multiplier in [0,10]. Contributions compose in descending priority, then ordinal plugin/local ID order. Combined multipliers above 100 reject the offending contribution. Encounter power/health pairs are validated atomically. All numbers must be finite and nonnegative.
- Ship and installation scopes are explicit. Scuttle denials prevent the native attempt before RNG and before armory destruction. Independent explosion denials prevent reactor explosion initiation without denying armory scuttle. Damage scaling alone does not prevent armory destruction or undo an initiated explosion/collapse. Cause scopes are per invocation and simulation, including nested combat/ammunition, hazards, grenades, scuttle and reactor explosion ticks. Authoritative host destruction bypasses all integrity reductions.

Host and metadata checks cover policy composition and adapter boundaries. Full native coexistence, damage, explosion and save/resume acceptance remains a separate milestone gate.
