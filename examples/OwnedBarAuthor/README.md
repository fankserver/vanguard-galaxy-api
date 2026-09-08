# Independent bar authors

`make build-bar-authors CONFIGURATION=Release` builds two separately loaded example assemblies, `OwnedBarAuthorA` and `OwnedBarAuthorB`, with distinct BepInEx IDs. Neither is included in the API package.

Enable API `[Bars] Enabled` in a disposable sandbox. Invoke `Register(stationGuid, localId, seed)` on each plugin, then `Place(currentSession, localId)`. Both may use the same local ID. `Configure` requests additive or exclusive station ownership; exclusive permission must be granted explicitly in the API configuration.

The API persists supported presentation state. These authors have no save hooks or serializers. `Release` removes runtime behavior but preserves persistent rows for later registration. The interaction counter is process-local example behavior, not persisted narrative state.

These are qualification inputs, not evidence that a native scenario passed. Do not install them into a production game.
