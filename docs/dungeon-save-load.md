# Dungeon save/load boundaries

API-owned authored definitions and choices use the `vgmodapi.dungeons` save provider. Consumers re-register their behavior; they do not serialize definitions or reconstruct saved occurrences themselves. Native location markers identify occurrences but do not contain their definitions. See [authored content](dungeon-content.md) and [save compatibility](compatibility.md).

## Native serialization audit

These mappings apply to the inspected game assembly identified in `compatibility.md`. They are source findings, not in-game qualification.

| Native boundary | Retained state | Limit |
|---|---|---|
| `DungeonSimulation.ToJson` / `FromJson` | Core progress, integrity, outcome and reasons, extraction/collapse state, level scaling, options, compartments, both crew lists, collected loot, event log, configuration flags, event state, unlock and lockdown timers | Pending crew directives are not serialized by these methods. A reconstructed simulation alone does not prove complete encounter continuity. |
| `SimCrewUnit.ToJson` / `FromJson` | Per-unit `isPlayerUnit`, alongside native crew state | Do not infer operation ownership solely from friendly/hostile list membership. |
| `BoardingPodData.DataToJson` / `LoadFromJson` | Pod identifier, phase, outbound crew, optional parent ship identifier, hull offsets and target positions | The runtime `_returnCrew` manifest is separate and absent from this payload. Outbound crew must not replace surviving return crew. |
| `DungeonManager.ReconstructSinglePod` | Docked, launching and attached pod reconstruction | Returning and arrived phases take the default null branch. |
| `DungeonOperation.RegisterReconstructedPod` | Native arrival/return subscriptions, launch counters and active-pod membership | Subscription restoration does not supply a missing return manifest. |
| `DungeonManager.EnsureApproachOperation` | Approach reconstruction for targets with pod data | Completed simulations refuse this path; it cannot recover every post-victory return obligation. |
| `DungeonManager.RecoverPendingExtraction` | Eligible surviving simulation crew returned through the native ship API | This is not a substitute for all in-flight return manifests. |
| `DungeonOperation.HandlePodCrewReturned` | Removes pod membership, applies crew with capacity handling and jettisons overflow | Removing subscriptions/membership precedes crew effects. An exception can leave partial effects, so retrying the whole call is unsafe. |

## Crew and directive supplements

With experimental Dungeons enabled, crew save/load hooks retain the six native execution fields omitted by the native serializer: assigned directive target, flee delay, withdrawal flag, retreat origin, fractional healing progress and daze timer. The bounded binary supplement is stored as base64 beside the corresponding native crew JSON, so it is restored to that unit rather than matched by crew type or faction.

Simulation JSON also retains pending directives. Claimants reference only the ordered crew arrays of that same snapshot. Restoration validates compartment references and claimant assignments after crew execution state is restored, before enabling simulation ticks. Invalid supplements quarantine the affected simulation and refuse its serialization; they are not rewritten as default state. Missing supplements retain native compatibility defaults, not proof of complete continuity for an older save. Host patch-entry tests cover save/restore/save; these hooks have not been qualified in Unity.

## Compatibility and removal policy

The recovery provider uses envelope schema 1, operation schema 5 and pod schema 1. Only these versions are accepted; no automatic conversion of unsupported recovery payloads is provided. Unknown schemas, corrupt payloads and inconsistent identities must not be replaced with empty obligations. Restore a compatible API version or a matching backup rather than editing markers to bypass validation.

A missing content provider does not erase retained authored definitions, but provider-owned choices require its registered behavior; see [authored content](dungeon-content.md). New API-owned creation requires restored, writable persistence. Missing original ships or live locations leave recovery staged, without substituting the current player ship or creating a replacement location.

Removing or disabling the API/recovery hooks removes their protection: vanilla does not interpret the supplemental return manifests or settlement receipts. There is no automatic removal/migration procedure for outstanding operations. Retain a matching native-save/sidecar backup and keep recovery enabled while those obligations remain. A save made without these hooks is not evidence that all crew or effects were settled.

## Supplemental return-state primitives

Experimental Dungeons wires operation/pod capture, identity markers, return receipts and a return reconstruction coordinator. Complete encounter recovery remains unqualified and incomplete. The recovery envelope retains independent operation and pod identities, the exact parent ship identity, phase, transport pose, outbound and return manifests, donor identity, reinforcement role, and separate attempted/delivered flags. A known-empty manifest is distinct from missing state. A potentially partial return attempt is not automatically retried or described as successful delivery.

The bounded provider payload rejects unsupported schemas, duplicate pod identities, invalid counts and trailing bytes. Restoring a different selected save replaces its obligations; a new game starts empty. Mutation requires restored, writable state in the current session. A marked pod whose supplemental record is missing cannot silently create a replacement record. Native recipient identity must match before return dispatch.

Marked initial operations are queued instead of using the current player ship as an implicit reconstruction donor. The queue waits for saved recipients/donors and hydrated simulation state, then uses native resume-only constructors. Approach options retain assigned crew, ammunition, stealth, automatic movement/buyout and priority compartment even before a simulation exists. Initial pods are built inactive, rebound to the operation and marked before activation; docked reinforcements restore their pending membership. Failed construction is not automatically retried. This integration is not yet covered by the full restoration scenario matrix or Unity qualification.

Walk approaches retain whether outbound visual dispatch has already started. Restoring that flag avoids dispatching the visual transfer again; native simulation entry performs the actual crew removal once. Cosmetic walker objects, animation position and remaining animation delay are not reconstructed. Native global outbound-walker waiting still applies if walkers exist in the current scene. A deserialized walk location remains staged until its exact live component belongs to the initialized current POI.

Approaching reinforcement donors retain their exact identity and already-reserved crew separately from spawned pods. Reconstruction rebinds native approach callbacks without collecting or subtracting crew again, waiting for the original donor and target. Native reservation/spawn effects refuse reentrant saves; uncertain effects block further mutation and serialization until reload. When a live donor's target disappears, native abort behavior is allowed under a save guard. Once native behavior has switched away from the approach, the matching reservation is retired in the same restore generation. Native abort does not refund reserved enemy crew; the API does not invent a refund. Missing donors remain unresolved rather than substituting another ship. These paths still require target-departure and abort integration qualification.

Pending walk extraction has a separate immutable crew-return obligation and pending/attempted/delivered progress. Marked locations do not use the vanilla direct-recovery shortcut. After world and simulation readiness, reconstruction restores Extraction without rerunning terminal rewards; completion uses native crew capacity/overflow handling and verifies delivery receipts. Failed or incomplete attempts are not retried after reload. Cosmetic inbound walkers are not reconstructed; native waiting for any existing inbound walkers still applies. This path still requires the full restore/exception matrix and Unity qualification.

These paths have host coverage only. Native-shaped return tests account for accepted roster additions and persisted overflow before marking delivery. Terminal attempts and mid-effect serialization refusal have host tests. Full initial-pod/reinforcement restoration testing, pending walk extraction integration testing, donor callback integration testing, terminal settlement continuity and native acceptance remain required before automatic encounter recovery can be claimed. Native and sidecar files are not an atomic transaction; no cross-file crash atomicity is promised.
