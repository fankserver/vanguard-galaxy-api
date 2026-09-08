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

## Supplemental return-state primitives

Experimental Dungeons wires operation/pod capture, identity markers, return receipts and a return reconstruction coordinator. Complete encounter recovery remains unqualified and incomplete. The recovery envelope retains independent operation and pod identities, the exact parent ship identity, phase, transport pose, outbound and return manifests, donor identity, reinforcement role, and separate attempted/delivered flags. A known-empty manifest is distinct from missing state. A potentially partial return attempt is not automatically retried or described as successful delivery.

The bounded provider payload rejects unsupported schemas, duplicate pod identities, invalid counts and trailing bytes. Restoring a different selected save replaces its obligations; a new game starts empty. Mutation requires restored, writable state in the current session. A marked pod whose supplemental record is missing cannot silently create a replacement record. Native recipient identity must match before return dispatch.

These paths have host coverage only. Native-shaped return tests account for accepted roster additions and persisted overflow before marking delivery. Terminal attempts and mid-effect serialization refusal have host tests. Complete initial-pod/reinforcement reconstruction, pending walk extraction, donor approach callbacks, terminal settlement continuity and native acceptance remain required before automatic encounter recovery can be claimed. Native and sidecar files are not an atomic transaction; no cross-file crash atomicity is promised.
