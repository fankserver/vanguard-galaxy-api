# Dialogue observation

`ModApi.Services.Dialogue` observes the game's conversation manager without controlling its narrative or audio. It initializes automatically when its native bindings are available.

## Supported channel

The service reports the selected line after `DialogueManager.ShowDialogueLine`, including the first line of ordinary/default conversations and subsequent/previous lines. Text and speaker are immutable snapshots of the native values at presentation time. Text selection/localization has already happened; the typewriter animation and line trigger may still be pending. Closed notifications follow native closure. Unloading the dialogue scene, session invalidation/replacement and API shutdown cancel outstanding presentation work.

This addresses TTS's conversation-line hooks and campaign checks of the conversation window. Bar roster text and ECHO remarks are separate channels: they are not silently treated as conversation-manager lines. Bar ownership/observation remains in the bar service. Captain voice presets, text normalization, portrait selection, synthesis, queueing and story choices remain consumer responsibilities.

## Observation and cooperative presentation

Subscribe to snapshots through `Subscribe`; read `Current` for the current line. Observations and service calls are main-thread-only. A conversation ID plus monotonically increasing sequence identifies a particular presentation. Identical repeated native notifications are deduplicated, while moving back to a previous line creates a new presentation sequence.

A speech provider can call `TryAcquirePresentation(ownerId, conversationId, sequence)`. Only the first claimant succeeds for that line, including after it disposes its claim. This prevents cooperative providers from speaking the same line twice. Owner labels are diagnostic, not authentication or permission to alter game state. The API never interrupts or replaces vanilla dialogue.

Use the returned cancellation token for asynchronous synthesis. After returning to the main thread, check `IsCurrent` before playback. A replaced line, closed window, scene/session change, disposal or shutdown invalidates the lease. A stale completion cannot reacquire an old line. Providers must honor cancellation; the API does not own their audio devices or task scheduler.

All snapshots, subscriptions and presentation claims are transient. This service does not record persistent choices or outcomes. Persisted story state belongs to the story API; additional narrative information remains custom mod data.
