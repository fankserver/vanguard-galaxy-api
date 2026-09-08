# Dungeon rewards and settlement

`ModApi.DungeonRewards` and `ModApi.DungeonSettlement` are optional experimental
services available from API 0.1.33 under `dungeon-rewards`, requiring boarding observation. Query capability
availability before accessing them. Native qualification remains pending: host and
installed-assembly checks are not evidence of complete in-game acceptance.

## Supported customization

Acquire an `IDungeonRewardProvider` with the consumer's plugin ID and register
local-ID policies for `LootAmount` or `MasteryExperience`. Registrations are
instance-owned and disposable. Providers may reuse local IDs across namespaces.
Policies run in ordinal provider/local-ID order, with each multiplier in [0, 10]
and combined multipliers bounded to 100. A failing callback is isolated; its
adjustment is ignored. Reentrant evaluation or a changed session preserves the
original native value. A registration disposed during its callback contributes
no adjustment. Commands and authored dungeon mutations refuse policy dispatch.

- **LootAmount:** modifies the resolved quantity at the native cargo-delivery
  boundary, after native partial-retreat selection. Fractional quantities round
  down. An unrepresentable result preserves the original count. Native capacity
  splitting and overflow world drops remain authoritative.
- **MasteryExperience:** modifies the native Leadership mastery award inside its
  exact boarding operation and recipient scope. The native defeat reduction is
  applied first; policies see the resulting award rather than converting defeat
  to victory.
- **Mission-token encounters:** reward adjustments do not run when either the
  location capture token or ship mission GUID identifies protected mission content.
- **Not customizable here:** capture ownership, mission success/failure, mission
  rewards, direct credit-item use, immediate data routing, guaranteed capture,
  returning crew manifests, brig capacity or deletion of native consequences.

These are adjustments to native execution, not extra grants. The API does not
retry a throwing native reward operation or rerun its terminal processing.
Unrelated or nested loot entries cannot borrow an outer entry's reward context.

## Observed settlement

`IDungeonSettlement.Get` returns a copied, session-local snapshot for a known
operation; `Subscribe` reports subsequent snapshots without replay. Capture is
recorded only from its native capture boundary. A friendly victory does not imply
capture, and resolved combat does not imply returning-crew delivery.

`Casualties` counts currently observed friendly units that are killed or have no
HP. `PrisonersDelivered` counts successful native brig additions observed for this
operation in this session, subtracting returned overflow; jettisoned prisoners
are not brig delivery. `CrewCountsObserved` distinguishes an unsampled operation
from an observed empty count. These are observational receipts, not a historical
save ledger or proof that every earlier delivery was observed. Native saved
simulation state and API-owned gameplay persistence are distinct from receipts.

`CrewReturnSettled` uses boarding observation's outstanding player-pod obligations
and successful return boundaries. Disappearing pods, empty pod collections or
operation retirement cannot establish crew settlement. Session changes clear
queries; consumers may retain the immutable snapshots as records.

Boarding reward events distinguish actual inventory, data-inventory, credit and
registered-world-drop applications from collected loot. A successful inner
application remains a fact if a later batch step throws. No event claims an entire
batch succeeded merely because its transfer method returned.

## Native capture invariants

Native capture remains responsible for faction changes, commander/crew clearing,
ammunition limits, module and hull-upgrade damage, captured-ship inventory,
mission-token exclusions, mission triggers and auto-claim, NPC state cleanup,
hangar preparation and world departure. The API does not implement capture by
assigning ownership. Defeat, retreat and explosion retain their native mission
failure and crew handling; investigation/extraction remain separate outcomes.
