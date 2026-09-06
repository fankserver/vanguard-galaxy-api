# Controlled Unity qualification runner

This is development tooling for issue #2, not part of the API distribution. Owner-approved test deployment is required. The owner's complete milestone acceptance remains a separate gate.

## Isolation

`tools/qualification.ps1` provisions a fresh Windows sandbox:

- Copies the launcher/Unity native runtime files and local BepInEx core.
- References the installed read-only game resources through directory junctions; does not redistribute them.
- Installs only VGModAPI, the observer example, and the opt-in qualification runner. Writes the inspected Doorstop 4 `[General]` / `target_assembly` format with a local preloader path.
- Copies two provided saves into a separate `Saves` directory and creates synthetic future-version/corrupt fixtures there.
- Records hashes of files in the **real save directory as well as fixture directories**, even when the supplied fixtures are already copies. The runner checks the protected directory against vanilla's actual pre-redirection SavesPath. Hashing covers direct files, matching the inspected flat save layout; nested directories are not covered.
- Records plugin SHA-256 hashes and an optional BuildRevision, refusing changed prepared binaries.
- Snapshots/restores the inspected title's shared PlayerPrefs registry key around Run. This is not full Windows-profile isolation: other persistent-profile writes are not redirected.

The runner refuses an unmarked sandbox or a different executable directory. It redirects both vanilla save-directory fields before testing and guards Store/Recall against non-sandbox destinations. Steam initialization is disabled in the isolated process to avoid achievements/stat changes or relaunching the installed game. The inspected `--fse-shim-applied` bootstrap marker prevents the game's fullscreen workaround from writing a registry entry and spawning an unowned child.

The game runs **windowed, not batch mode**: the inspected vanilla GlobalControls reads Mouse.current, which can be null in batch mode. This tooling is not a security sandbox against malicious plugins or paths. It omits other mods except the explicitly selected consumer pilots below; no general multi-mod compatibility is implied. Use `-Action Cleanup` to unlink the three resource junctions non-recursively. It preserves private evidence and local copies; never recursively delete a sandbox while those junctions still exist.

## Build and prepare

Build from WSL/Linux with the installed reference paths available:

```sh
make package CONFIGURATION=Release
```

The solution builds `QualificationRunner.dll` and `QualificationGuard.dll`, but the API package allowlist excludes both. On Windows, run the self-authored PowerShell script from a **local** path if RemoteSigned treats a WSL UNC path as remote; do not disable machine execution policy. Keep `qualification-profile.ps1` and `qualification-inputs.ps1` beside the launcher; copy the test scripts with their relative directory layout.

```powershell
.\qualification.ps1 -Action Prepare `
  -SandboxRoot "$env:TEMP\VGModAPI-qa-unique-run" `
  -SaveA "C:\path\to\source-a.save" `
  -SaveB "C:\path\to\source-b.save" `
  -BuildRoot "C:\path\to\vanguard-galaxy-api" `
  -OriginalSaveDir "$env:USERPROFILE\AppData\LocalLow\Bat Roost Games\VanguardGalaxy\Saves" `
  -BuildRevision "<built commit; note dirty builds explicitly>"

.\qualification.ps1 -Action Run `
  -SandboxRoot "$env:TEMP\VGModAPI-qa-unique-run"
```

`OriginalSaveDir` defaults to the inspected title's normal Windows save directory and must exist. It is independent of SaveA/SaveB. `GameDir` can override the installed game location. The game must not already be running. Every attempt requires a new directory; do not reuse failed evidence. The launcher bounds process lifetime (1800 seconds by default, configurable via TimeoutSeconds) and cleans up only processes at that sandbox's exact executable path. Keep the process budget longer than the sequence's bounded per-stage waits.

## API-independent isolation and startup-negative modes

`QualificationGuard.dll` is a separate development-only bootstrap with no API assembly reference. The API declares an optional BepInEx ordering dependency on its ID; the runner requires it. When the guard is absent, normal API installation is unchanged. When present with the explicit sandbox flag, it verifies the real save-directory manifest, redirects saves, guards Store/Recall, and suppresses Steam before API initialization. The runner checks its arming receipt. Both tools remain excluded from the distributed API package.

Select a mode during **Prepare** using `-Scenario` (the prepared mode is recorded and checked at Run):

- `Full` (default): guard, API, observer, and the full runner.
- `MissingApi`: guard only; reach the menu, verify no API plugin loaded, then quit.
- `UnavailableApi`: guard and API only. A scoped Harmony postfix substitutes a zero hash result from `ReadAssemblyHash` before API Awake. Require one injection, an existing service with both integration capabilities unavailable, and no API-owned Harmony patches, then quit.

The mismatch is **injected input**, not an altered or alternate game DLL. It checks the live rejection path, not compatibility with another game version. Selected pilots additionally test consumer dependency refusal as described below. Run verifies the exact flat plugin set, hashes, and scenario before launch; extra files/directories or reparse-point plugins are rejected. A guard must remain active through quit-time writes. Do not deploy legacy plugins that can write before this ordering boundary; consumer coexistence requires its own reviewed setup.

## Evidence and coverage

Local output includes `events.tsv` (with failure detail/exception type when supplied by the API), `result.txt`, game/BepInEx logs, process/provenance receipts, and preservation markers. A PASS result is meaningful only together with successful save/prefs preservation and log inspection. A run-start receipt or pre-existing preference snapshot prevents retry from overwriting recovery evidence; restore failures get a separate private receipt even if save verification also fails. **Treat every raw artifact as private**, including TSV details, stack traces, receipts, and registry snapshots: they may contain usernames, full paths, or preferences. Publish only reviewed, redacted excerpts.

The automated sequence checks copied loads, replacement, manual save/roundtrip, ephemeral skip, exhausted retries, subscribers and rejection/recovery. The extended sequence adds four calls through vanilla's autosave-slot selector, one narrowly scoped metadata-write exception to exercise successful retry, and equal empty-player fixtures with newer/current headers. It drives the native new-game wizard callbacks, observes its synchronous configuration boundary, and attempts a fresh space-save roundtrip. These are scripted callback checks, not pointer-driven UI acceptance; consult the recorded results before treating a new scenario as exercised. It uses vanilla GameManager.LoadGame rather than assuming an iterator factory completing means readiness. Session-id checks prevent stale-session acceptance; an unscaled two-second settle grace permits remaining startup work and time scale is not frozen for saving. This is a harness heuristic, not a universal world/UI-ready guarantee.

The modal probe maps the inspected original `Behaviour.UI.AlertPopup.ShowMessage(string, string, Action)` method, private static `activeInstance`, static `IsOpen` getter, and private `submitButton` field. `CreateButton` registers `DestroyPopup` then the confirmation callback on `submitButton.onClick`; destruction clears `activeInstance` and unpauses. Reinspect the installed assembly at the recorded hash rather than relying on older workspace decompilations, which may lack these members.

Expected rejection popups must be acknowledged before proceeding: observe the scoped `ShowMessage` key, invoke only that popup's actual confirmation button (including vanilla callbacks), and wait for destruction/menu cleanup. Do not dismiss unknown dialogs or advance the new-game wizard behind a modal. The owner observed a stale newer-save warning over the wizard in earlier runs (#35); those runs remain callback/event evidence, not clean modal/UI-flow acceptance.

A failed check must remain failed until its cause is investigated. Record bootstrap/harness failures separately from reproduced API defects. No test edits the allowed game hash or claims all-world readiness.

Run `tools/tests/qualification.Tests.ps1` on Windows for synthetic-file tests of manifest coverage, the DLL allowlist, preloader configuration, provenance, path/reuse refusal, and junction cleanup. These tests do not launch Unity. `tools/tests/qualification-profile.Tests.ps1` separately exercises snapshot/restore and backup verification using a unique synthetic registry key, never the real game's preferences. The corrected smoke passed in qa-06 with verified original-save and PlayerPrefs preservation. Read-only startup diagnostics require the inspected synchronous `void GameplayManager.Start` signature and can be enabled with `-Diagnostics` on Run; qa-06 deliberately omitted them. The valid-syntax newer-version fixture must end without readiness, not merely throw a parser error. That event alone does not uniquely identify the too-new-version branch. qa-09 additionally exercised an equal empty-player fixture with the current header, which instead failed in nested deserialization.

Earlier qa-01 through qa-04 runs predated PlayerPrefs snapshots. Their save checks do **not** prove unchanged settings; display preferences may have changed and no before-snapshot exists to restore them reliably.

qa-09 also passed synchronous wizard configuration, mining-space save/load, autosave rotation and successful retry recovery. Later controlled probes cover transit/empty-space loads, explicit stale adapter signals and the two authorized consumers. Pointer-driven UI acceptance, arbitrary async engine callbacks and actual alternate game binaries remain separate; injected hash rejection is not alternate-version compatibility. The inspected autosave selector chooses the first missing slot, then the oldest file by modification time; the four-call 0/1/2/0 expectation assumes distinct timestamps on the tested filesystem and may fail on coarse-resolution filesystems. Do not set RuntimeQualified merely because the automated subset passes.

## Authorized MissionJournal pilot

Prepare accepts optional `-MissionJournalBin <owner-built Release/netstandard2.1 directory>`. Only VGMissionJournal.dll and Newtonsoft.Json.dll are added; never copy installed legacy plugins blindly. Cecil verifies journal identity/version 0.2 and the hard `vgmodapi` version constructor before launch, because legacy startup sweeping precedes the old API-dependent isolation. This metadata check is not review or binary provenance evidence. Keep the exact hashes and source revision in the private prepared manifest.

All consumer scenarios require existing companion journal sidecars for both supplied saves and copy them into the sandbox. Negative startup runs hash-check the complete sandbox save-file set after exit to verify that disabled consumers did not touch those sidecars. Reflection-only probes compare nonempty persisted history IDs with the public facade after slot switching/reload, reject prior-slot-only history, compare successful destination sidecars with live history, and reject failed/skipped-save writes. New game must not inherit old history; destroying only the journal component must unsubscribe and prevent subsequent sidecar writes. No real save/sidecar is a write target. Counts/IDs are not exported publicly.

With the same optional consumer selection, MissingApi requires BepInEx dependency refusal. UnavailableApi requires a present but disabled journal, no public facade and no journal-owned patches. It remains an injected hash-rejection test, not alternate-binary compatibility. Prepared consumer markers and the scenario-specific flat plugin set are verified before launch.

## Authorized Stockpile pilot

Optional `-StockpileBin <owner-built Release/netstandard2.1 directory>` adds Stockpile0.6 (hard API0.1.1 dependency) and Newtonsoft. Both consumers may be selected; shared Newtonsoft bytes must agree. A sandbox-only config enables transfers. Existing transfer companions are copied; missing companions produce empty synthetic fixtures. Negative scenarios verify selection/refusal and the entire copied file set.

The full pilot pauses the transfer driver before gameplay to inspect copied queues. It then explicitly clears only the disposable in-memory queue, without refunding inventory, for controlled real station request/cancel/fee tests. It checks save/reload alignment, failed-save refusal, protected/corrupt sidecars, retry, slot replacement and component/UI/driver teardown. A manual near-completion tick is followed by actual Unity driver delivery. This is not pointer-driven transfer-dialog acceptance or uncontrolled natural-time testing. Original saves, inventories and credits are never write targets.

## Remaining core-path probes

After consumer teardown, the full sequence attempts a normal in-system route, saves/reloads while travelling, then creates a controlled parked-space snapshot using vanilla cancellation/completion calls and verifies the empty Space load. A separate Unity-driven production observed iterator injects delayed adapter readiness/failure signals after a normal replacement load, then explicit disposal; it does not claim arbitrary asynchronous engine callback coverage. The direct adapter BeginLoad/EndLoadRequest pair adds a synthetic invalidation/start to the trace and must not be counted as a vanilla load. qa38 passed the strengthened probes; preserve the distinction between controlled fixture/injection coverage and general engine behavior.

Run results and source revisions belong in the pilot PRs. This tooling does not qualify all mission domain hooks, pre-readiness starter grants, TravelJournal coexistence or the rest of milestone 01.
