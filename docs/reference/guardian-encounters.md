# Authored guardian encounters

The encounter group can author a named boss/guardian unit at a combat site with dynamic
threat-based level scaling, a forced loadout rarity, and an outgoing-damage overclock that
bridges the game's item-level cap. This is an additive extension of the persistent combat-site
machine ([world content](world-content.md)); it does not add a competing spawning mechanism.

Authoring initializes with the world/encounter group. `ModApi.Services.World` and the typed
`IWorldProvider.SpawnEncounter` remain the entry points; the guardian loadout options are
optional fields on `EncounterComposition`. Availability is the shared encounter/world
availability; inspect it before relying on authored spawning.

## Additive options on `EncounterComposition`

- `EncounterLevelPolicy? LevelPolicy` — dynamic scaling. When either aspect is set, the spawn
  level resolves at materialisation as `min(max(floor, playerLevel + threatOver), game ceiling)`
  instead of the fixed `Level`. When it is left null the fixed level is used unchanged (existing
  behavior, no added ceiling).
  - `int? LevelFloor` — absolute minimum resolved level; defaults to the composition's fixed level.
  - `int? ThreatOver` — player-relative threat bonus.
- `AuthoredLoadout? Loadout` — applied to each spawned unit-data instance:
  - `AuthoredRarity? Rarity` — forces the unit to that equipment rarity and re-rolls its loadout
    (`Standard` / `Enhanced` / `HighGrade` / `Exotic` / `Legendary`, mirroring native names).
  - `int? RequestedDamageLevel` — a target outgoing-damage tier. The API raises the unit's outgoing
    damage from its item-level-capped effective level up to that tier (no-op when already met), using
    the official damage curve as a per-unit stat boost, idempotently by an API-owned id.

The overclock and loadout forcing are **runtime-only**: nothing is written to unit data or saves,
and the spawned units remain transient session content. A required persistence failure therefore
never applies to these options; they only attach to authored spawns that have already been
validated.

## Guarded boundaries

- Scaling and loadout use the game's own helpers (`GameMath.ApplyItemLevelCap`/`DamageMultiplier`/
  `maxLevel`) and the existing per-unit stat-boost path. These members are bound against the
  inspected game hash; an unrecognized assembly disables game integration. Do not add a new hash
  without reinspection.
- Hostility remains scoped to the spawned units only; an existing faction's diplomacy is never
  modified. Killing the units costs no reputation only when `NoReputationLoss` is set.
- Only the units the author owns through this surface are affected; the player's ship and other
  same-class ships are untouched.
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
    loadout: new AuthoredLoadout(AuthoredRarity.Legendary, requestedDamageLevel: 140));
var result = provider.SpawnEncounter(poiId, comp);
```

Sustain the guardian with the existing surfaces — `World.UnitProtection.Protect(unitId)` to keep it
alive and `World.DroneBays.Tune(unitId, …)` for its drone swarm — using the spawned unit-data
identity. See [world content](world-content.md) and [service contracts](service-contracts.md).
