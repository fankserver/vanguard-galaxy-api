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

The installed-CONSUMER metadata checks of the actual-consumer qualification probe need an owner-built consumer binary, so they are excluded from `make test` and from `check-local` and run separately:

```sh
make check-consumer ANIMA_ASSEMBLY=/path/to/VGAnima.dll
```

Each consumer has its own test class, so a run with only one built binary selects it with `CONSUMER_FILTER`. They read that assembly's metadata and IL with Cecil only: they pin every consumer member the probe reflects, the consumer's own hard API dependency, and the two structural facts the probe's conclusions rest on (the visited-system map can only grow inside the public-event observer, and the consumer owns no native travel/station Harmony patch). They launch nothing, load nothing into the test process and never copy a consumer or dependency binary into this repository.

Decoding the consumer's custom-attribute ENUM arguments (`BepInDependency`'s dependency flags and `JsonProperty`'s `NullValueHandling`) forces Cecil to RESOLVE two of the consumer's compile-only references, so the resolver gets an explicit bounded search path instead of Cecil's implicit `.`/`bin` probing: the candidate's own output directory, the consumer project's `lib` reference-link directory, this test project's output directory, and the directories the target passes in `VG_CONSUMER_DEPENDENCY_DIRS` — the installed `BepInEx/core` (BepInEx.dll), the installed `VanguardGalaxy_Data/Managed` references, and the local NuGet copy of Newtonsoft.Json (`NUGET_PACKAGES_DIR`/`NEWTONSOFT_DIR`, both overridable). Nothing outside that list is read. When a dependency lives elsewhere, add its directory instead of moving files:

```sh
make check-consumer ANIMA_ASSEMBLY=/path/to/VGAnima.dll ANIMA_DEPENDENCY_DIRS=/path/with/Newtonsoft.Json.dll:/path/with/BepInEx.dll
make check-consumer ECHO_ASSEMBLY=/path/to/VGEcho.dll ECHO_DEPENDENCY_DIRS=/path/with/BepInEx.dll
```

An unresolvable dependency fails with the exact assembly name, the directories that were searched and that override, never with a bare Cecil stack trace.

The Windows launcher reads the same consumer metadata during `Prepare`, and needs the same bounded reference set for the same reason: `Read-ConsumerAssembly` in `qualification-inputs.ps1` searches only the candidate's own directory, the sandbox `game\BepInEx\core` (BepInEx.dll) and the installed `VanguardGalaxy_Data\Managed` references, with Cecil's implicit probing removed, the candidate opened in memory and both the assembly and the resolver disposed. That set is required, not decorative: decoding a custom attribute's ENUM argument forces Cecil to resolve the assembly declaring the enum, and an unresolved enum does not throw - the attribute silently decodes to ZERO arguments. `qa-87` refused a correct Echo build for exactly that reason (`BepInDependency(..., DependencyFlags.SoftDependency)` needs BepInEx.dll; Anima's two-string declaration never did), so an undecodable blob is now reported as a reader-configuration failure and never as a wrong consumer shape. Host synthetics cannot reproduce a missing resolver, so `tools/tests/qualification.Tests.ps1` carries an opt-in integration regression that reads a REAL built consumer assembly when `VG_QUALIFICATION_ECHO_DLL` (optionally with `VG_QUALIFICATION_GAME_DIR`) is set; default runs stay self-contained, copy nothing and launch nothing. Resolution matches by assembly NAME (Cecil does not compare versions), so a locally cached 13.0.x Newtonsoft stands in for the exact compile-time patch version; that is metadata decoding only and never a compatibility claim.

After building the package, `make provenance` emits the actual SDK, source revision/dirty flag, requested configuration, and SHA-256 for each local reference and the three packaged assemblies. It refuses compile-reference links pointing at a different installation. The standalone command is an input/output snapshot, not proof of a prior compiler invocation; use `check-local` to build/check immediately before reporting. It emits neither absolute installation paths nor binary contents. Publish that bounded report and check results, not a reference bundle or raw qualification profile. A recorded hash identifies bytes; it does not establish licensing or live compatibility.

## Packaging

`make package` removes the old package directory, copies three explicitly named owned assemblies plus documentation, then runs `make check-package`. `PackageChecks` defines the complete file allowlist. Missing/extra files, unexpected directories, links, wrong assembly identities and unexpected dependencies in any of the three assemblies fail validation. Update the explicit document list deliberately when adding packaged documentation.

`make check-package` inspects an already-created package without repairing it. This is useful to prove that injected stale/reference files fail validation. It needs no installed game to inspect the files, but constructing the full package does require the lawful local references above. Public layout tests use synthetic files and do not pretend to validate a built plugin.

No proprietary/reference DLL, observer example DLL, qualification runner DLL, or debug output belongs in the package. Package checks do not authenticate arbitrary binaries supplied by an adversary; build reviewed source first.

Unity smoke evidence and the remaining runtime matrix stay in [compatibility](compatibility.md). Neither passing CI nor package validation changes RuntimeQualified.
