# Restricted world qualification authors

`make build-world-authors CONFIGURATION=Release` builds two independent assemblies, `WorldAuthorA.dll` and `WorldAuthorB.dll`. Both register `PoiX` through the public abstraction in their own `Awake`, using distinct authenticated provider identities. They reference neither game assemblies nor API internals and install no patches.

Registration alone does not create world content. A separately reviewed qualification runner must invoke `Create`/`Find` with the current session and selected instance/system, check every result, and exercise ordinary save/load. `Release` supports the provider-loss control. No automatic deployment or launcher is supplied by this target.

These probes require the isolated world qualification candidate and its complete authorized sandbox context. They are not production mods, native acceptance evidence, or permission to run the game. Story-reference and broader content probes remain separate requirements.
