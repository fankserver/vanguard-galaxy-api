# Visible pocket rendering in Conquest sectors — WIP tracking

> **Status: WIP.** This file tracks an open question about whether a `Visible`-placement
> pocket system renders as its own map dot inside a **Conquest** sector. We are testing one
> thing at a time and updating this document as findings land.

## Question

`PocketSystemPlacement.Visible` places the pocket in the **anchor system's own sector** and
renders it as a distinct dot on the settled belt/galaxy map. Earlier in-game testing
suggested the dot **did not** appear when the anchor sector was a **Conquest** sector,
even though it rendered fine in a normal (non-Conquest) sector. We want to confirm the real
behavior before deciding whether any change is needed.

## Corrected finding: dots are drawn for every system in every sector

Reading the shipped game assembly (`Behaviour.GalaxyMap.GalaxyMapManager.RefreshSectorMap`),
the sector map instantiates a `WorldMapStatic` dot for **every** `SystemMapData` in
**every** sector — Conquest sectors included — through the same loop. Conquest sectors only
*additionally* draw territory polygons (`DrawConquestPolygons`) on top of those dots.

This means a system added normally (which is exactly what `CreatePocketSystem(..., Visible)`
does via `CreateEmptySystem`) **should** render as a dot everywhere, including Conquest
sectors. The earlier "Conquest can't render a dot" note appears to have been a false
conclusion — most likely from testing the **OffMap** wormhole world (an intentionally blank,
remote sector where nothing is drawn) rather than an actual Visible pocket inside a Conquest
sector.

## Design principle (owner direction)

> "If a dot is normal, then we should add a dot — always. We do not want to hack any game
> mechanic. If following the game mechanic is required, we will follow it."

So: the Visible pocket should behave as **a normal system dot in all sectors**, using the
game's own system-into-sector path. No special-casing, no hidden-map tricks, no polygon
hacks. If a dot renders for vanilla systems in a Conquest sector, our Visible pocket must
too, because it is created the same way.

## One-at-a-time test plan

Test only one thing per step; record the outcome before moving on.

1. **Dot is normal / baseline.** Confirm a vanilla (non-authored) system shows a dot when
   the sector map is open in a normal sector. (Baseline sanity.)
2. **Visible in a normal sector.** Spawn only the Visible pocket in a normal anchor sector;
   confirm its own dot appears in-cluster view.
3. **Visible in a Conquest sector.** Fly to a Conquest sector, spawn only the Visible pocket
   there, open the in-cluster view, and check whether its dot appears. This is the open
   question this file tracks.
4. **Conquest baseline.** Also open the in-cluster view of a Conquest sector with NO authored
   pocket, and note whether vanilla systems in it show dots. This isolates whether Conquest
   sectors normally render dots at all (our decomp says yes).

## How to run the test (no code change needed)

The `WormholeWorld` example already exposes separate `Spawn Visible Pocket` and
`Spawn Wormhole World (OffMap)` controls. The Visible control anchors to your **current**
sector, so testing Conquest requires no new code:

```text
1. Fly to a Conquest sector you want to test.
2. Open the plugin's panel and press "Spawn Visible Pocket" (only that one).
3. Open the in-cluster/belt map view and look for the pocket's dot in that sector.
```

Do **not** fly the OffMap wormhole when checking the Visible dot — that world is off-map and
blank by design, and confusing it with the Visible pocket is what misled the earlier test.

## Outcome (to fill in)

- [ ] Step 3 result: does the Visible pocket show a dot in a Conquest sector? (Expect: yes,
  per the decomp.)
- [ ] If yes → the earlier note was wrong; no code change needed; we already always add a dot.
- [ ] If no → capture a screenshot, then investigate whether any Conquest-specific draw path
      hides dots before proposing a fix (owner principle: follow the mechanic, no hacks).
