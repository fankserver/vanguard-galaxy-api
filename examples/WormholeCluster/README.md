# Wormhole Cluster example

A sample/test BepInEx mod demonstrating authored **pocket-cluster + wormhole + themed-site**
authoring together, plus the **full-cleanup** surface: every owned occurrence (system, gate,
wormhole, site) dissolves back out.

## What it builds

From your current system `X`, press **Spawn Wormhole** to open a wormhole into a small authored
cluster that lives in **its own subsector** — it is not mixed into your sector's systems or gate
network. The cluster subsector is assembled by combining placements:

- **Entry (E)** is `OffMap`, so it allocates a fresh, remote subsector — the cluster itself — and
  names it ("Wormhole Cluster") via the definition's `SectorName`.
- **Hub (A)**, **Anchor (B)** and **Mining** are `Visible` anchored to a *cluster* system; `Visible`
  places a pocket in its anchor's own subsector, so they land **inside E's subsector**.
- **Salvage** is `OffMap`, giving it its own subsector ("Salvage Drift") outside the cluster.

```
 [ Wormhole Cluster subsector ]
   E (Cluster Entry)  --gate-->  A (Hub Alpha)
                   \--gate-->  B (Anchor Beta)     B is a dead-end
   A  --[gate back to E]--  + two wormholes:
          * Mining Instance  (inside this subsector, a mining-field site)
          * Salvage Instance (its own subsector, a salvage wreck site)

 X --wormhole-->  E        (the only way in from your system)
```

- **Entry system (E)** — reached from `X` only through the entry wormhole; its own anchored gate to
  `X` is sealed *and hidden*, so no gate line is drawn for it. E connects by gate to both A and B
  ("only 2 gates").
- **Hub Alpha (A)** — a gate back to E, and **two wormholes** into separate themed instances:
  a mining-only one (inside the cluster) and a salvage-only one (outside). Each is seeded with a
  single site kind so the instance is "all one theme".
- **Anchor Beta (B)** — just the gate back to E; no content (a dead-end).
- **Static names** — every system has a fixed, unchanging display name (Cluster Entry, Hub
  Alpha, Anchor Beta, Mining Instance, Salvage Instance) plus the two named subsectors.

## Buttons

| Button | Action |
|---|---|
| **Spawn Wormhole** | Creates the whole cluster (entry wormhole + E/A/B + two off-world wormholes + both sites). |
| **Delete Cluster** | Full cleanup: dissolves the wormhole pairs first (a pocket that is still a wormhole endpoint cannot dissolve), then each pocket — its gate and site POIs go with it. Nothing authored remains. |

### Cleanup ordering

`Delete Cluster` dissolves in dependency order:
1. The three **wormhole pairs** (entry `X↔E`, and A→mining, A→salvage) — the endpoints must be
   freed before their pockets can dissolve.
2. The **off-world pockets** (their site rows drop with the pocket).
3. The branch systems **A** and **B**, then the **entry E**.

If the player is inside any part of the cluster, a dissolve is refused until they leave (a safe
guard, not a silent failure).

## Requirements it demonstrates

- Pocket-system **chaining** (a pocket anchored to another pocket's system id, giving E its two
  gates to A and B).
- **Wormhole-pair** creation and **dissolution** (entry + per-off-world doors), including that a
  pocket that is still a wormhole endpoint refuses to dissolve.
- **Quiet wormholes** (`quiet: true`): an owned rift spawns no decorative passerby traffic and no
  security patrol at either end — a private door, not a highway.
- **Resource sites** (mining + salvage) placed **inside** owned pockets, removed with the pocket.
- **Static** system names, plus named subsectors.
- **Full cleanup** / tear-down of everything spawned.

## Build

```bash
dotnet build examples/WormholeCluster/WormholeCluster.csproj
```

Deploy `bin/Debug/netstandard2.1/WormholeCluster.dll` into `BepInEx/plugins/`.
