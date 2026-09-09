# Developer documentation

- [Contributing: feature delivery and correctness tests](../../CONTRIBUTING.md)
- [Native integration constraints](native-integration.md)
- [API reference](../reference/README.md)
- [Build and test commands](../../Makefile)
- [Release workflow](../../.github/workflows/release.yml) and [publisher](../../tools/publish_update.py)

Implement the feature and its direct API tests together. There is no qualification,
preparation or native-acceptance workstream. The current implementation and its
functional limitations belong in the reference docs, not in probe reports.

Release publication requires explicit authorization. Do not run standalone publishers
concurrently or edit reserved discovery assets manually: GitHub provides no atomic
compare-and-swap for advancing those assets.

`docs/reference/` and `docs/assets/` ship with the API; this directory does not.
