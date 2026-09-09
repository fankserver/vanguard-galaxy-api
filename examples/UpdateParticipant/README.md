# Update participation example

Build/package from the repository root with `make example-update-package CONFIGURATION=Release EXAMPLE_VERSION=1.2.3` and lawful local compile references. The single example version property generates the loader attribute and assembly version; feed generation verifies the packaged DLL. The target is a publication dry run only.

The archive contains this plugin and its JSON metadata, not API/loader/game DLLs. Replace `example/mod` URLs before distribution. A declared dependency is enough for listing; networking is centralized in VGModAPI and optional.

See [the author publication guide](../../docs/reference/mod-update-publishing.md) for numeric versions, stable/prerelease URLs, release ordering, failure recovery and testing. Never edit a player's configuration to publish a release feed.
