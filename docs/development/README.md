# Developer documentation

- [Contributing: feature delivery and correctness tests](../../CONTRIBUTING.md)
- [Native integration constraints](native-integration.md)
- [In-game end-to-end regression tests](e2e-tests.md)
- [API reference](../reference/README.md)
- [Build and test commands](../../Makefile)
- [Release workflow](../../.github/workflows/release.yml) and [build/upload command](../../tools/release.sh)

Implement the feature and its direct API tests together. There is no qualification,
preparation or native-acceptance workstream. The current implementation and its
functional limitations belong in the reference docs, not in probe reports.

## Prereleases

With owner authorization, run `gh release create vX.Y.Z --target main --prerelease
--latest=false --generate-notes`. The release workflow checks out that numeric tag,
validates/builds it, and attaches the ZIP and SHA256. The release page is public
before the assets finish building. A trusted runner labelled `vgmodapi-release`
with local game/BepInEx references is required; GitHub-hosted runners cannot build
this package.

Without that runner, check out the tag in a clean trusted worktree and run
`bash tools/release.sh vX.Y.Z` locally. This is the same build/upload command, not
a separate publisher. Existing asset names are never overwritten. After a partial
upload, verify the existing asset against the local bytes and upload only the
missing file with `gh release upload`; do not use `--clobber`.

The `stable` archive suffix is the numeric-tag package convention, not a GitHub
release promotion. This process never uploads `update.json`, advances update
feeds, or marks a release latest. Stable promotion is a separate authorized task.
Author package validation remains available via `make example-update-package`.

`docs/reference/` and `docs/assets/` ship with the API; this directory does not.
