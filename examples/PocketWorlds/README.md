# Pocket Worlds

**What you can build with this: your own star systems.** Systems the base game never generated —
chained together, hidden off the drawn map, reachable only through wormholes you placed, filled with
your own mining fields, salvage wrecks and hostile encounters — and removable again without a trace.

A sample/test BepInEx mod for the **VG Mod API** (v0.2.8+). One in-game button opens an authored
**pocket cluster**; a **Log topology** button prints exactly what was wired; **Delete Cluster**
removes everything it created.

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
| **Resource sites** (`CreateResourceSite`) | A mining field in one off-world and a station-bearing salvage wreck in the other, both removed with the pocket that holds them. |
| **Nested dungeon lifetime** (`IDungeonProvider.Attach`) | At the salvage wreck, **Attach station layout** adds a minimal authored dungeon to its station. Removing the pocket later removes the station and retained dungeon state automatically—no separate cleanup call. |
| **Occurrence state** | The HUD status line reads each occurrence's live reconstruction state. |
| **Topology diagnostics** | Spawning (and the **Log topology** button) writes one line per system: the gates and wormholes it holds with their far end, plus any site inside — the ground truth to compare against the in-game map. |
| **Full cleanup** (`Remove` / `CanRemove` / `RequestRemoval`) | **Delete Cluster** removes each pair, then each pocket, in the order the API's integrity rules force. Anything not removable yet is queued for the next safe cleanup window instead of acting under the player. |

## What you see

From your current system `X`, press **Spawn Wormhole**. The HUD panel becomes three buttons:
**Delete Cluster**, **Log topology**, and a status line showing each owned occurrence's live
reconstruction state (`E:A:B:M:S | door:m:s`).

## The topology it wires

```
            [ Wormhole Cluster subsector ]
              E (Cluster Entry) --gate--> A (Hub Alpha)
                              \--gate--> B (Anchor Beta)      (quiet dead-end)
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
| **Spawn Wormhole** | Creates the whole cluster: entry wormhole + E/A/B + the two off-world wormholes + the two resource sites. |
| **Attach station layout** | At the salvage wreck, passes the owned `IResourceSite` to `IDungeonProvider.Attach`; the API resolves that site's station rather than guessing among observed targets. |
| **Log topology** | Writes the authored wiring to `BepInEx/LogOutput.log` — compare it against the in-game map if a connection looks surprising. |
| **Delete Cluster** | Full cleanup in dependency order: wormhole pairs first (a pocket that is still a wormhole endpoint cannot be removed), then each pocket with its gate and resource sites. If you are inside any part of the cluster, the affected step is **queued** for the next safe cleanup window rather than acting under you. |

## Occurrences belong to one session

An occurrence object from an ended or replaced session keeps its **last observed state** and never
resolves against the replacement save. So this example drops every handle on `SessionInvalidated` and
re-obtains the cluster with `GetPocketSystem` / `GetWormholePair` / `GetResourceSite`
at `GameplayInitialized`. Those calls create nothing — the API already restored the
occurrences from save data; they only hand back a handle. Anything the loaded save does not contain
stays absent.

Without this the panel would happily report a cluster the loaded save never had.

## Lifetime note

Provider acquisition happens in `Start()`, not `Awake()`. BepInEx only populates
`Chainloader.PluginInfos[].Instance` *after* a plugin's `Awake()` returns, and the world provider
authenticates the caller against exactly that entry — so acquiring in `Awake()` cannot succeed.
Every example in this repository that acquires an instance-authenticated provider (world, story,
bars, items, recipes) follows the same rule.

## Teardown order, and why it is not taste

Every authored object exposes `Remove()`, `CanRemove()` and `RequestRemoval()`. The order **Delete
Cluster** uses is forced by the API's integrity rules:

| Rule | Consequence |
|---|---|
| A pocket that is still a wormhole endpoint cannot be removed | all three pairs go first |
| A pocket's **resource** sites *are* removed with it | the mining/salvage site handles are simply dropped |
| An attached dungeon is part of its boarding location | removing the station-bearing salvage pocket also removes its retained dungeon state |

`Remove()` is the **plain** removal — it refuses only on integrity grounds and does *not* check
whether the player is standing in what you are deleting. This example therefore asks `CanRemove()`
before every step and, when the answer is not `Ready`, calls `RequestRemoval()` so the step completes
at the next safe cleanup window. Any deferral stops the cascade, so a half-torn cluster is never
reported as cleared.

## Build & deploy

```bash
dotnet build examples/PocketWorlds/PocketWorlds.csproj
```

Deploy `bin/Debug/netstandard2.1/PocketWorlds.dll` into `BepInEx/plugins/`.

Requires the VG Mod API plugin (≥ **0.2.8**). This example is never part of the shipped API package.
