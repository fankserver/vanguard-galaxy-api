# Developer documentation

- [Contributing](../../CONTRIBUTING.md)
- [Native integration findings](native-integration.md)
- [Controlled Unity qualification](qualification-runner.md)
  - Bar scopes: [API](bar-probe-scope.md), [consumers](bar-consumer-scope.md), [linked story](bar-linked-scope.md)
  - [Absent story author](story-absence-scope.md)
- [API reference](../reference/README.md)
- [Release workflow](../../.github/workflows/release.yml) and [publisher](../../tools/publish_update.py)

Release publication requires explicit authorization. Do not run standalone publishers
concurrently or edit reserved discovery assets manually: GitHub provides no atomic
compare-and-swap for advancing those assets.

`docs/reference/` and `docs/assets/` ship with the API; this directory does not.
