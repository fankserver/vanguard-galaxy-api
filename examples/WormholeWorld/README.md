# Wormhole World example

A sample/test BepInEx mod for the **VG Mod API** (v0.2.10+). It is a deliberately complete
worked example: one in-game button opens an authored **wormhole cluster** — several owned pocket
systems chained together and linked to your current system by wormholes — then a **Log topology**
button prints exactly what was wired, and **Delete Cluster** removes everything it created.

Read it as a tour of what the API can do, not just a demo: every capability it touches is the
"show everything" covering of the world-authoring + ambient-traffic surfaces.

## What it demonstrates (the abilities)

| Ability | How the example uses it |
|---|---|
| **Pocket systems** (`CreatePocketSystem`) | Five authored systems: an entry, a hub, a dead-end anchor, and two themed off-world instances. |
| **Pocket chaining** | A pocket anchored not to your system but to *another pocket's* id — which is what gives the entry its two gates (E→A, E→B). |
| **Wormhole pairs** (`CreateWormholePair`) | Three pairs: the entry rift `X↔E`, and A→mining, A→salvage. Each pair is exactly connected to itself, so it never leaks into the global wormhole mesh. |
| **Placement** (`PocketSystemPlacement`) | `OwnSector` (its own named subsector inside the zoomable frontier band), `Visible` (inside an anchor's subsector), and `OffMap` (a real, saved, working system sited outside the drawn map — reachable only by wormhole). |
| **Named subsectors** (`SectorName`) | The cluster subsector ("Wormhole Cluster") and the off-map one ("Salvage Drift"); without this the game generates a procedural name. |
| **Static names** | Every system keeps a fixed display name (Cluster Entry, Hub Alpha, …) each spawn. |
| **Quiet wormholes & systems** (`quiet: true`) | The rifts spawn no decorative passerby traffic and no security patrol at either end; `quiet: true` on a pocket keeps its whole system silent — the cluster is a private place, not a highway. |
| **Sealed hidden gates** | Each pocket's anchored "gate back" is closed *and* hidden, so the map draws no phantom gate line. (Only the deliberate E→A, E→B gates are open/visible.) |
| **Resource sites** (`CreateResourceSite`) | A mining field in one off-world and a salvage wreck in the other, both removed when their pocket removes. |
| **Topology diagnostics** | Spawning (and the **Log topology** button) writes one line per system: the gates and wormholes it holds with their far end, plus any site inside — the ground truth to compare against the in-game map. |
| **Full cleanup** (`Remove`, plural) | **Delete Cluster** resolves each owned wormhole pair then each pocket, so no authored system, gate, wormhole or site remains. |

## What you see

From your current system `X`, press **Spawn Wormhole**. The HUD panel becomes three buttons:
**Delete Cluster**, **Log topology**, and a status line showing each owned occurrence's live
reconstruction state (`E:A:B:M:S | door:m:s`).

## The topology it wires

```
            [ Wormhole Cluster subsector ]
              E (Cluster Entry) --gate--> A (Hub Alpha)
                              \--gate--> B (Anchor Beta)      (B is a quiet dead-end)
              A --[gate back to E]-- + two wormholes:
                    * Mining Instance   (inside this subsector; mining-field site)
                    * Salvage Instance  (its own OFF-MAP subsector; salvage-wreck site)

   X --wormhole--> E        (the only way in from your system)
```

- **Entry (E)** — `OwnSector`: allocates its own named subsector; no sector jump gate, so the
  subsector is on the map but reachable only via the wormhole.
- **Hub (A)** — a gate back to E, plus the two wormholes into themed off-worlds.
- **Anchor (B)** — just the gate back to E (a dead-end).
- **Mining / Salvage** — one-instance worlds; "one inside, one off".

## The HUD buttons

| Button | What it does |
|---|---|
| **Spawn Wormhole** | Creates the whole cluster: entry wormhole + E/A/B + the two off-world wormholes + both sites. |
| **Log topology** | Writes the authored wiring to `BepInEx/LogOutput.log` — compare it against the in-game map if a connection looks surprising. |
| **Delete Cluster** | Full cleanup in dependency order: wormhole pairs first (a pocket that is still a wormhole endpoint cannot remove), then each pocket with its gate and site. If you're inside any part of the cluster, a remove is refused until you leave — a safe guard, not a silent failure. |

## Using it to verify

1. Press **Spawn Wormhole**. In the log, read the `=== Wormhole World topology ===` block.
2. Fly the entry wormhole from `X` → `E`, then E → A, then the wormholes into Mining and Salvage.
3. Press **Log topology** whenever you want a fresh readout of what's connected and what each
   system contains (including static names vs. any procedurally named star/planet the game shows).
4. Fly back out, press **Delete Cluster**, and confirm no authored system, gate, wormhole or site
   remains (the log's `Wormhole World deleted` line).

## Build & deploy

```bash
dotnet build examples/WormholeWorld/WormholeWorld.csproj
```

Deploy `bin/Debug/netstandard2.1/WormholeWorld.dll` into `BepInEx/plugins/`.

Requires the VG Mod API plugin (≥ **0.2.10**). This example is never part of the shipped API
package.
