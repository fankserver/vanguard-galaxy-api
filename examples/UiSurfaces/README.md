# UI Surfaces: settings and saved progress

A self-contained example combining a hosted Unity window, shared HUD launcher,
Forge inspector, typed mod settings, and custom per-save data.

**Requires the matching API development build with Mod Settings. Public 0.2.8 does
not contain the settings surface.** Build this example and the API from the same
checkout; do not distribute it as a 0.2.8-compatible consumer.

## Try it

1. Open **Mods > UI Surfaces example > Settings**. The **Window** group publishes
   all four setting types: **Count window opens** (bool), **Visit goal** (integer),
   **Window opacity** (float), and **Color theme** (choice). The **Behavior** group
   adds **Show visit goal** (bool), demonstrating multiple group headers. **Reset all**
   restores every default.
2. Start or load a game. Use the top-right **Example window** HUD button. Each open
   increments this save's counter when counting and save-data mutation are allowed.
3. Change the goal, opacity, or theme. The existing window updates immediately.
   Pause counting without clearing the counter.
4. Save normally, then reopen the window to make another unsaved increment. Reload
   the saved game: the saved count returns, not the unsaved increment. New games
   start at zero; loading another save restores that save's own count.
5. Open the Forge and use **Inspect** to display recipe requirements and output
   previews. That inspector is observational and is not persisted.

Use a disposable test campaign when exploring saving behavior; the sample does not
trigger saves, change save paths, or load saves on your behalf.

## Two different kinds of persistence

| Data | Owner / lifetime |
|---|---|
| Count toggle, goal display toggle, goal, opacity, theme | BepInEx config (`vgmodapi.example.ui-surfaces.cfg`), global across games and restarts. The settings API uses getter/setter callbacks; it does not store another copy. |
| Window-open count | VGModAPI `SaveData`, per save. Registered once before a session starts; captured on supported game saves and restored by the API on load/new game. |
| Window visibility, Forge inspector | Temporary UI state, discarded with the UI host/session. |

The window explicitly distinguishes in-memory changes from durable saves. When
save data is blocked or inaccessible it shows the state and refuses counting. A
failed registration does not create a fake in-memory persistence fallback.

## Files to copy or adapt

- `Plugin.Settings.cs`: five explicit `ConfigEntry`-backed registrations across two groups. Settings
  acquire their authenticated provider in `Start`, after BepInEx's `Awake` setup.
- `WindowVisits.cs`: Unity-free custom save-data example. Four-byte nonnegative
  counter, schema version 1; invalid payloads are rejected, not silently reset.
  Every mutation checks `CanMutate`; inaccessible data is not displayed as zero.
- `Plugin.cs`: hosted window, subscription cleanup, shared HUD and immediate config
  updates. Save data registers in `Awake`, before any gameplay session.
- `Inspector.cs`: Unity-free Forge/recipe/HUD consumer, unchanged by the settings
  example. Captures the selected variant and reports refusals honestly.

The host tests compile the actual `WindowVisits.cs` and use the API generation
store to test save/load across service recreation, independent slots/new games,
unsaved changes, failed saves, blocked writes, invalid data and teardown. They do
not write to game saves. The `ui-surfaces` live case verifies the window and settings;
it keeps the player ephemeral and does not claim native disk-save validation.

## Build

Use `make build` from the API repository to refresh local references and build the
examples. With maintainer authorization, install `UiSurfaces.dll` alongside the
matching API, without copying API/reference DLLs into the example folder. Examples
are excluded from the API release package.
