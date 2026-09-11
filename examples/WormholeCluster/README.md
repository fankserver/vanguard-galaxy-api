# Wormhole Cluster example

A sample/test BepInEx mod demonstrating authored **pocket-cluster + wormhole + themed-site**
authoring together, plus the **full-cleanup** surface: every owned occurrence (system, gate,
wormhole, site) dissolves back out.

## What it builds

From your current system `X`, press **Spawn Wormhole** to open a wormhole into a small authored
cluster of three gate-linked owned pocket systems. The three systems (E/A/B) use **Visible**
placement, so they render as distinct dots on the belt/galaxy map (in/near your sector); the two
off-world instances stay **OffMap** — reachable only through their wormhole from Hub Alpha:

```
 X  --wormhole-->  E (Cluster Entry)   --gate-->  A (Hub Alpha)
                                    \--gate-->  B (Anchor Beta)

 A  --[gate back to E]--   + two wormholes into themed off-world instances (OffMap):
                                * "Mining Instance"  (a mining-field site)
                                * "Salvage Instance" (a salvage wreck site)
 B  --[gate back to E]--   nothing else (a quiet dead-end anchor)
```

- **Entry system (E)** — reached from `X` only through the entry wormhole; its own anchored gate to
  `X` stays sealed. E connects by gate to both A and B ("only 2 gates").
- **Hub Alpha (A)** — a gate back to E, and **two wormholes** each leading into a separate themed
  off-world pocket: a mining-only instance and a salvage-only instance. Each off-world is seeded
  with one site kind so the instance is "all one theme".
- **Anchor Beta (B)** — just the gate back to E; no content (a dead-end).
- **Static test names** — every system has a fixed, unchanging display name (Cluster Entry, Hub
  Alpha, Anchor Beta, Mining Instance, Salvage Instance).

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
- **Resource sites** (mining + salvage) placed **inside** owned pockets, removed with the pocket.
- **Static** system names.
- **Full cleanup** / tear-down of everything spawned.

## Build

```bash
dotnet build examples/WormholeCluster/WormholeCluster.csproj
```

Deploy `bin/Debug/netstandard2.1/WormholeCluster.dll` into `BepInEx/plugins/`.
