# Authored encounter equipment & scaling

The encounter group can author a spawn at a combat site that applies dynamic threat-based level
scaling, a forced equipment rarity, and an outgoing-damage overclock that bridges the game's
item-level cap. This is an additive extension of the persistent combat-site machine
([world content](world-content.md)); it does not add a competing spawning mechanism.

`IWorldProvider.SpawnEncounter` remains the entry point; the extra options are optional fields on
`EncounterComposition`. Availability is the shared encounter/world availability; inspect it before
relying on authored spawning.

## Additive options on `EncounterComposition`

- `EncounterLevelPolicy? LevelPolicy` — dynamic level scaling. When either aspect is set, the spawn
  level resolves at materialisation as `min(max(floor, playerLevel + threatOver), game ceiling)`
  instead of the fixed `Level`. When left null the fixed level is used unchanged (existing behavior,
  no added ceiling).
  - `int? LevelFloor` — absolute minimum resolved level; defaults to the composition's fixed level.
  - `int? ThreatOver` — player-relative threat bonus.
- `EncounterEquipmentOverride? EquipmentOverride` — applied to each spawned unit-data instance:
  - `ItemRarity? OverrideRarity` — forces the unit to that equipment rarity and re-rolls its loadout
    (`Standard` / `Enhanced` / `HighGrade` / `Exotic` / `Legendary`, mirroring the native
    `Source.Item.Rarity` names; distinct from unit rank `EncounterRank`).
  - `int? OverrideDamageLevel` — a target outgoing-damage tier. The API raises the unit's outgoing
    damage from its item-level-capped effective level up to that tier (no-op when already met), using
    the official damage curve as a per-unit stat boost, idempotently by an API-owned id.

The equipment override and overclock only mutate the **spawned** unit's transient data at
materialisation: the overclock writes an entry into `AbstractUnitData.statBoosts` (a non-serialized
field) and the rarity write sets `SpaceShipData.shipRarity` (a serialized field, but on a transient
spawned unit that is never persisted by the API). So the save-safety conclusion holds for API-owned
content, but it is a consequence of the data being transient unit data, not of avoiding all writes —
nothing the API owns is written to a save.

## Result: owned unit identities

`SpawnEncounter` returns an `EncounterSpawnResult` whose `UnitIds` (an `IReadOnlyList<string>`)
surfaces the persistent unit-data identities of the units scheduled, in schedule order, for the
units that carry one. This is the identity the existing protection and drone-bay surfaces take, so
an author can **sustain the spawned boss**:

```csharp
var result = provider.SpawnEncounter(poiId, comp);
if (result.Succeeded)
    World.UnitProtection.Protect(result.UnitIds[0]);  // keep the boss alive
```

## Guarded boundaries

- Scaling and the overclock use the game's own helpers (`GameMath.ApplyItemLevelCap`/
  `DamageMultiplier`/`maxLevel`) and the existing per-unit stat-boost path. Every native member is
  declared in the `EncounterEquipmentBindings` catalog with its full declared arity and is shape-
  checked by the InstalledGame Cecil tests (run by `make check-bindings`), so a game reshape fails
  the build instead of drifting silently. Do not add a new hash without reinspection.
- Hostility remains scoped to the spawned units only; an existing faction's diplomacy is never
  modified. Killing the units costs no reputation only when `NoReputationLoss` is set.
- Only the units the author owns through this surface are affected; the player's ship and other
  same-class ships are untouched.
- A binding/reshape failure of the equipment seam only degrades this optional feature (rarity,
  overclock and dynamic scaling fall back to vanilla); it never unpins world protection or any other
  capability.
- The authoritative source of the scaling formula is the game. The policy resolver is mirrored in
  host tests; if the game formula changes, the runtime binding must be re-inspected and the mirror
  updated together.

## Consumer example

```csharp
var comp = new EncounterComposition(
    new[] { new EncounterWave(1f, "Redemption", 1) },   // one boss, 1s after activation
    "rogue",
    level: 92,
    rank: EncounterRank.Elite,
    hostileToPlayer: true,
    noReputationLoss: true,
    levelPolicy: new EncounterLevelPolicy(levelFloor: 92, threatOver: 20),  // floor 92, player + 20
    equipmentOverride: new EncounterEquipmentOverride(ItemRarity.Legendary, overrideDamageLevel: 140));
var result = provider.SpawnEncounter(poiId, comp);
if (result.Succeeded && result.UnitIds.Count > 0)
    World.UnitProtection.Protect(result.UnitIds[0]);   // sustain the boss
```

See [world content](world-content.md) and [service contracts](service-contracts.md).
