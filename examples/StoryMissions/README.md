# Story Missions

**What you can build with this: your own missions.** Hand-authored campaign beats with decisions the
player actually makes, jobs generated from runtime text, and follow-up missions that offer themselves
when an arc completes — all saved and restored by the API, with no save hook of your own.

A sample/test BepInEx mod for the **VG Mod API** (v0.2.10+). One HUD panel drives the whole mission
lifecycle so every step is reachable in game.

## The lifecycle it walks

```
register (Start)  ->  Offer  ->  Activate  ->  SetProgress  ->  DeclareChoices  ->  Completed
                                                                                       |
                                                                  follow-up Offer  <---+
```

## What it demonstrates (the abilities)

| Ability | How the example uses it |
|---|---|
| **Owned story provider** (`AcquireProvider`) | Acquired in `Start()` with its refusal diagnostic reported, not swallowed. |
| **Hand-authored campaign** | "A witness's account": two steps, a scripted listen objective (×3) and a reply, `StoryRetention.Campaign`, credits + experience rewards, and a `completionText`. |
| **Declared choices** (`choiceKeys` / `DeclareChoices`) | The reply records a `witness` choice. The API persists it; this plugin owns no serializer. |
| **Generated jobs** | The job's pitch text is supplied at runtime and uses exactly the same owner-scoped API as authored content — `StoryRetention.Temporary`, so it is not kept as a campaign outcome. |
| **Mixed objective kinds** | `Scripted` (mod-driven), `CollectCredits` (game-observed, counting) and `ReturnToSource` (needs no invented POI id). |
| **Objective keys** (`WithKey`) | The counting objective is addressed by a stable key rather than by position. |
| **Absolute progress** (`SetProgress`) | Progress is read from the snapshot and set absolutely. `Progress` is nullable — unknown is reported as unknown, never assumed to be zero. |
| **Gameplay reactions** (`Completed`) | Completing the campaign offers the follow-up **in the game that produced the event** (`mission.Game`) — no current-game lookup and no session token. |
| **Typed results** | Offer/activate/abandon results are surfaced in the status line instead of being treated as fire-and-forget. |
| **No provider persistence** | No save hook, serializer or load callback anywhere: the API restores occurrences, progress and choices. |

`StoryReactions.cs` keeps the same pattern in its smallest possible form — a definition-scoped
`Completed` subscription that offers a follow-up through the completed mission's own game — for
lifting into your own mod.

## The HUD buttons

| Button | What it does |
|---|---|
| **Offer campaign** | Offers and activates the authored campaign beat in the current game. |
| **Hear the witness (+1)** | Advances the scripted `talk` objective by one, absolutely. |
| **Answer the witness** | Completes the reply objective and declares the `witness` choice. |
| **Offer generated job** | Offers and activates the runtime-generated temporary job. |
| **Abandon active missions** | Abandons whatever is active and reports the typed result. |

Complete the campaign and the follow-up mission is offered automatically — the status line's
follow-up counter increments without you pressing anything.

## Lifetime note

The story provider is acquired in `Start()`, not `Awake()`. BepInEx only populates
`Chainloader.PluginInfos[].Instance` *after* `Awake()` returns, and the story provider authenticates
the caller against exactly that entry — acquiring in `Awake()` cannot succeed.

## Build & deploy

```bash
dotnet build examples/StoryMissions/StoryMissions.csproj
```

Deploy `bin/Debug/netstandard2.1/StoryMissions.dll` into `BepInEx/plugins/`.

Requires the VG Mod API plugin (≥ **0.2.10**). Use disposable saves when trying example content; do
not deploy examples into an existing campaign without explicit intent and backups. This example is
never part of the shipped API package.
