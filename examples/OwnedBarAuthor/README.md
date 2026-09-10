# Independent bar authors

`make build-bar-authors CONFIGURATION=Release` builds two separately loaded example assemblies, `OwnedBarAuthorA` and `OwnedBarAuthorB`, with distinct BepInEx IDs. Neither is included in the API package.

Enable API `[Bars] Enabled` in a disposable sandbox. Invoke `Register(stationGuid, localId, seed)` on each plugin; the API places the declared contact automatically once the game and its save data are ready — there is no per-session placement call. Both plugins may use the same local ID. `Configure` requests additive or exclusive station ownership; exclusive permission must be granted explicitly in the API configuration.

The API persists supported presentation state. These authors have no save hooks or serializers. `Release` removes runtime behavior but preserves persistent rows for later registration; explicit per-save removal is available on the live patron object (`game.Bars.Get(definition).Remove()`), persists as absence, and is reversible with `Restore()`. The interaction counter is process-local example behavior, not persisted narrative state.

These examples demonstrate authenticated provider composition. Use disposable saves when trying them; do not install example content into an existing campaign without explicit intent and backups.
