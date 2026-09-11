# Wormhole World example

A loadable BepInEx plugin (requires Mod API **0.2.10**) demonstrating the **authored pocket
systems** and **authored wormhole pairs** surfaces working together, exposing them through the
HUD as clickable rows rather than Unity UI.

## What it does

While flying, open the "Wormhole World" HUD panel. It shows:

| Button | Effect |
|---|---|
| **Spawn Wormhole World** | Creates an authored pocket system anchored next to your current system, then links the two with a native `AuthoredWormholePair`. The pocket's own jump-gate entrance is closed so the wormhole pair is the only door. |
| **Close Door / Open Door** | `SetOpen(false/true)` on the pair — seals or reopens both ends together without changing the exact connection. |
| **Add a mining field in the pocket** | Places an `AuthoredSite` (mining field) inside the pocket so it has a little content. |

## The "same door back" property

The authored pair is **exactly connected to itself**: it creates two native `Wormhole` POIs and
targets each end only at its owned peer. Fly into the door that spawns near your location to
cross the rift into the pocket "world". The pocket-side wormhole is the **same door** — using it
carries you back to the system you came from. The pair never joins the global wormhole mesh and
does not depend on wormhole-unlock progression.

## Build

The example is built automatically by `make build` via the `examples/*/*.csproj` wildcard, or
standalone with:

```bash
dotnet build examples/WormholeWorld/WormholeWorld.csproj
```

Deploy `bin/Debug/netstandard2.1/WormholeWorld.dll` into `BepInEx/plugins/`.

## Notes

- Definitions (`RegisterPocketSystem` / `RegisterWormholePair` / `RegisterResourceSite`)
  are registered once in `Start()`, before any game session starts; content is created only on
  explicit player input during gameplay.
- **Register in `Start()`, not `Awake`.** BepInEx only assigns `Chainloader.PluginInfos[].Occurrence`
  *after* a plugin's `Awake` returns, so host authentication (which matches your plugin occurrence in
  `PluginInfos`) — and therefore `AcquireProvider` — cannot succeed inside `Awake`. `Start()` runs
  after BepInEx finishes loading but still before the first session, which is exactly when
  providers may be acquired.
- If the wormhole pair cannot be created, the freshly made pocket is dissolved (`Dissolve`) so a
  half-open world is never left behind.
- Registers buttons by returning `clickable: false` rows for completed/disabled actions and only
  enables the rows that are actionable right now.
