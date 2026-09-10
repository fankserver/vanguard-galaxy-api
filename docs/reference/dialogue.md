# Dialogue observation and story characters

`ModApi.Services.Dialogue` observes the game's conversation manager without controlling its narrative or audio, and its `Characters` service introduces and extends named story characters. Each initializes automatically when its native bindings are available; their availability is independent.

## Supported channel

The service reports the selected line after `DialogueManager.ShowDialogueLine`, including the first line of ordinary/default conversations and subsequent/previous lines. Text and speaker are immutable snapshots of the native values at presentation time. Text selection/localization has already happened; the typewriter animation and line trigger may still be pending. Closed notifications follow native closure. Unloading the dialogue scene, session invalidation/replacement and API shutdown cancel outstanding presentation work.

This addresses TTS's conversation-line hooks and campaign checks of the conversation window. Bar roster text and ECHO remarks are separate channels: they are not silently treated as conversation-manager lines. Bar ownership/observation remains in the bar service. Captain voice presets, text normalization, portrait selection, synthesis, queueing and story choices remain consumer responsibilities.

## Observation and cooperative presentation

Subscribe to snapshots through `Subscribe`; read `Current` for the current line. Observations and service calls are main-thread-only. A conversation ID plus monotonically increasing sequence identifies a particular presentation. Identical repeated native notifications are deduplicated, while moving back to a previous line creates a new presentation sequence.

A speech provider can call `TryAcquirePresentation(ownerId, conversationId, sequence)`. Only the first claimant succeeds for that line, including after it disposes its claim. This prevents cooperative providers from speaking the same line twice. Owner labels are diagnostic, not authentication or permission to alter game state. The API never interrupts or replaces vanilla dialogue.

Use the returned cancellation token for asynchronous synthesis. After returning to the main thread, check `IsCurrent` before playback. A replaced line, closed window, scene/session change, disposal or shutdown invalidates the lease. A stale completion cannot reacquire an old line. Providers must honor cancellation; the API does not own their audio devices or task scheduler.

All snapshots, subscriptions and presentation claims are transient. This service does not record persistent choices or outcomes. Persisted story state belongs to the story API; additional narrative information remains custom mod data.

## Named story characters

`ModApi.Services.Dialogue.Characters` makes story characters exist as ordinary clickable
NPCs, and attaches owner content to characters the game already owns. The game rebuilds
characters freshly on every registry lookup; declarations are consulted at that moment
and again on every click, so content survives save/load and repeated station boarding
without consumer patches, caching or bookkeeping, and nothing is written to saves.

```csharp
var characters = ModApi.Services.Dialogue.Characters;
// A character the game does not have. Place ricko.LookupName wherever the game
// expects a character name, such as a station's persisted character list.
_ricko = characters.Introduce(pluginId,
    new StoryCharacterDefinition("ricko", "Ricko", "Luminate Ship Mechanic",
        portraitOf: "QuestgiverHullBlueprints"), // registry name; displayed in game as Voss
    conversation: () => CurrentStep switch
    {
        DeliveryPending => new CharacterConversation(new[]
        {
            CharacterLine.Self("Still need those canisters."),
            CharacterLine.Captain("We're on it."),
        }),
        Handoff => new CharacterConversation(new[] { CharacterLine.Self("That's everything. Thank you.") },
            completed: CompleteDelivery),
        _ => null,
    },
    missionHighlights: new[] { MissionId });
// Owner content on a game character, without owning its identity.
_arle = characters.Extend(pluginId, "LuminateCommander",
    conversation: () => OfferAvailable ? OfferConversation() : null,
    missionHighlights: new[] { MissionId });
```

- **Introduce** creates an owner-scoped character. Its `LookupName` embeds the provider,
  so two mods introducing similar names can never collide or capture each other's NPC;
  the display name is the definition's `Name`. The conversation callback is asked for
  the current conversation on each click — a natural place for arc state machines — and
  null shows nothing. Same-provider duplicate local identities are rejected; disposing
  a registration frees its identity for re-declaration.
- **Extend** attaches to the registry name of a game-owned character (for example
  `LuminateCommander`, whose display name is Arle). Extensions are consulted in
  registration order before the character's own default dialogue; the first
  conversation wins and null falls back to vanilla. Pending native trigger dialogues
  keep their vanilla priority over default dialogue, exactly as they do today. Multiple owners may extend one character; one owner's
  fault or disposal never silences another owner or the character itself.
- Lines speak as the character (`Self`), the player's captain, the ship AI, or any
  named character — including other introduced ones via their lookup names. Named
  references use **registry names** (the factory names the game's registry resolves,
  like `LuminateCommander`), not display names like Arle. Portraits are reused from a
  named game character the same way; an unknown registry name leaves the portrait
  unset and there is no asset-path surface. `Completed`
  runs once when the conversation finishes and is the place to advance mission state.
- `missionHighlights` lists native mission identities for the game's own offer marker;
  whether the marker shows still follows the game's mission state.

Every unrelated lookup stays completely vanilla. While the integration is unavailable,
declarations are retained but resolve nothing, and extended characters behave stock.
Consumer callbacks are isolated: a fault is reported once and that click falls back to
vanilla. Registration, disposal and callbacks are main-thread-only. Character content
is transient API-owned presentation; persisted mission progress belongs to the story
API and saves are never written by this service. A station list entry naming an owned
`LookupName` is consumer-placed data with the same lifetime rules as any other entry
there; when the owner is absent, the game's registry simply reports that name unknown,
as it does today for any missing character.
