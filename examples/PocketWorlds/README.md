# Pocket Worlds

**What you can build with this: your own star systems.** Systems the base game never generated —
chained together, hidden off the drawn map, reachable only through wormholes you placed, filled with
your own mining fields, salvage wrecks and hostile encounters — and removable again without a trace.

A sample/test BepInEx mod for the **VG Mod API** (v0.2.10+). One in-game button opens an authored
**pocket cluster**; a **Log topology** button prints exactly what was wired; **Delete Cluster**
dissolves everything it created.

Read it as a tour of the world surface, not just a demo: it is the "show everything" covering of the
world-creation + ambient-traffic surfaces.

## What it demonstrates (the abilities)

| Ability | How the example uses it |
|---|---|
| **Pocket systems** (`CreatePocketSystem`) | Five authored systems: an entry, a hub, a guarded dead-end anchor, and two themed off-world instances. |
| **Pocket chaining** | A pocket anchored not to your system but to *another pocket's* id — which is what gives the entry its two gates (E→A, E→B). |
| **Wormhole pairs** (`CreateWormholePair`) | Three pairs: the entry rift `X↔E`, and A→mining, A→salvage. Each pair is exactly connected to itself, so it never leaks into the global wormhole mesh. |
| **Placement** (`PocketSystemPlacement`) | `OwnSector` (its own named subsector inside the zoomable frontier band), `Visible` (inside an anchor's subsector), and `OffMap` (a real, saved, working system sited outside the drawn map — reachable only by wormhole). |
| **Named subsectors** (`SectorName`) | The cluster subsector ("Wormhole Cluster") and the off-map one ("Salvage Drift"); without this the game generates a procedural name. |
| **Static names** | Every system keeps a fixed display name (Cluster Entry, Hub Alpha, …) each spawn. |
| **Quiet wormholes & systems** (`quiet: true`) | The rifts spawn no decorative passerby traffic and no security patrol at either end; `quiet: true` on a pocket keeps its whole system silent — the cluster is a private place, not a highway. |
| **Sealed hidden gates** | Each pocket's anchored "gate back" is closed *and* hidden, so the map draws no phantom gate line. (Only the deliberate E→A, E→B gates are open/visible.) |
| **Resource sites** (`CreateResourceSite`) | A mining field in one off-world and a salvage wreck in the other, both removed when their pocket dissolves. |
| **Combat sites** (`RegisterCombatSite` / `CreateCombatSite`) | A persistent owned combat site guarding Anchor Beta, keyed by an author-local occurrence key; the API allocates the native identity and reconciles the same key to the same object after a reload. |
| **Occurrence state** | The HUD status line reads each occurrence's live reconstruction state, including the combat site's. |
| **Topology diagnostics** | Spawning (and the **Log topology** button) writes one line per system: the gates and wormholes it holds with their far end, plus any site inside — the ground truth to compare against the in-game map. |
| **Full cleanup** (`Dissolve`, plural) | **Delete Cluster** resolves each owned wormhole pair then each pocket, so no authored system, gate, wormhole or site remains. |

## What you see

From your current system `X`, press **Spawn Wormhole**. The HUD panel becomes three buttons:
**Delete Cluster**, **Log topology**, and a status line showing each owned occurrence's live
reconstruction state (`E:A:B:M:S | door:m:s | guard`).

## The topology it wires

```
            [ Wormhole Cluster subsector ]
              E (Cluster Entry) --gate--> A (Hub Alpha)
                              \--gate--> B (Anchor Beta)      (quiet dead-end + combat site)
              A --[gate back to E]-- + two wormholes:
                    * Mining Instance   (inside this subsector; mining-field site)
                    * Salvage Instance  (its own OFF-MAP subsector; salvage-wreck site)

   X --wormhole--> E        (the only way in from your system)
```

- **Entry (E)** — `OwnSector`: allocates its own named subsector; no sector jump gate, so the
  subsector is on the map but reachable only via the wormhole.
- **Hub (A)** — a gate back to E, plus the two wormholes into themed off-worlds.
- **Anchor (B)** — the gate back to E, plus an owned combat site (a guarded dead-end).
- **Mining / Salvage** — one-instance worlds; "one inside, one off".

## The HUD buttons

| Button | What it does |
|---|---|
| **Spawn Wormhole** | Creates the whole cluster: entry wormhole + E/A/B + the two off-world wormholes + both resource sites + the combat site. |
| **Log topology** | Writes the authored wiring to `BepInEx/LogOutput.log` — compare it against the in-game map if a connection looks surprising. |
| **Delete Cluster** | Full cleanup in dependency order: wormhole pairs first (a pocket that is still a wormhole endpoint cannot dissolve), then each pocket with its gate and sites. If you're inside any part of the cluster, a dissolve is refused until you leave — a safe guard, not a silent failure. |

## Lifetime note

Provider acquisition happens in `Start()`, not `Awake()`. BepInEx only populates
`Chainloader.PluginInfos[].Instance` *after* a plugin's `Awake()` returns, and the world provider
authenticates the caller against exactly that entry — so acquiring in `Awake()` cannot succeed.
Every example in this repository that acquires an instance-authenticated provider (world, story,
bars, items, recipes) follows the same rule.

## Cleanup asymmetry (deliberate)

Pocket systems and wormhole pairs expose `Dissolve()`. Resource sites and combat sites do not: they
live inside a system and are removed with the pocket that holds them. That is why **Delete Cluster**
only dissolves pairs and pockets, and simply drops its site handles.

## Build & deploy

```bash
dotnet build examples/PocketWorlds/PocketWorlds.csproj
```

Deploy `bin/Debug/netstandard2.1/PocketWorlds.dll` into `BepInEx/plugins/`.

Requires the VG Mod API plugin (≥ **0.2.10**). This example is never part of the shipped API package.
