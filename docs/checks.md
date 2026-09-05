# Check strategy

## Public CI: no game assets

`Checks` runs `make test` in Debug and Release on clean Ubuntu runners using the SDK in `global.json`. CI installs the listed SDK; local `latestPatch` roll-forward can select a newer patch, so compare the recorded actual SDK before claiming identical toolchains. It asserts `VGModAPI/lib` is absent before and after testing and supplies a nonexistent game directory. These tests build only Abstractions/Core and synthetic adapters; they include package-layout regression tests and a metadata check that the stable contract references only netstandard.

A separate Windows job runs the fake-file and synthetic-registry harness tests under Windows PowerShell. It does not launch the game or read its real profile. Actions are commit-pinned, token permissions are read-only, and checkout credentials are not persisted. No workflow provisions or uploads game references, private profiles, or sandbox output.

Public CI is not a full plugin build, a real-package validation, an installed-game binding check, or Unity qualification.

## Owner-local checks with lawful references

Use your own installed game plus BepInEx 5.x. Set `GAME_DIR` to that local installation; never obtain an unknown publicized stub to make CI green. Build uses only local BepInEx/Harmony and Unity compile references, not a compile-time Assembly-CSharp stub. Binding checks separately inspect the original installed Assembly-CSharp. `make link-libs` creates ignored local links, not distributable copies.

With .NET 10, GNU make, and Python 3.11+ available:

```sh
make check-local CONFIGURATION=Debug
make check-local CONFIGURATION=Release
```

This runs pure tests, the full plugin/example/runner build, clean package creation and real-package inspection, installed metadata checks, then reference provenance. Nothing deploys or launches Unity. Never run this privileged/reference-bearing path on untrusted PR code. It is intentionally not an automatic self-hosted job.

After building the package, `make provenance` emits the actual SDK, source revision/dirty flag, requested configuration, and SHA-256 for each local reference and the three packaged assemblies. It refuses compile-reference links pointing at a different installation. The standalone command is an input/output snapshot, not proof of a prior compiler invocation; use `check-local` to build/check immediately before reporting. It emits neither absolute installation paths nor binary contents. Publish that bounded report and check results, not a reference bundle or raw qualification profile. A recorded hash identifies bytes; it does not establish licensing or live compatibility.

## Packaging

`make package` removes the old package directory, copies three explicitly named owned assemblies plus documentation, then runs `make check-package`. `PackageChecks` defines the complete file allowlist. Missing/extra files, unexpected directories, links, wrong assembly identities and unexpected dependencies in any of the three assemblies fail validation. Update the explicit document list deliberately when adding packaged documentation.

`make check-package` inspects an already-created package without repairing it. This is useful to prove that injected stale/reference files fail validation. It needs no installed game to inspect the files, but constructing the full package does require the lawful local references above. Public layout tests use synthetic files and do not pretend to validate a built plugin.

No proprietary/reference DLL, observer example DLL, qualification runner DLL, or debug output belongs in the package. Package checks do not authenticate arbitrary binaries supplied by an adversary; build reviewed source first.

Unity smoke evidence and the remaining runtime matrix stay in [compatibility](compatibility.md). Neither passing CI nor package validation changes RuntimeQualified.
