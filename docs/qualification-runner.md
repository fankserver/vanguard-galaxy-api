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

By default the mismatch is **injected input**, not an altered or alternate game DLL. It checks the live rejection path, not compatibility with another game version. Selected pilots additionally test consumer dependency refusal as described below. Run verifies the exact flat plugin set, hashes, and scenario before launch; extra files/directories or reparse-point plugins are rejected. A guard must remain active through quit-time writes. Do not deploy legacy plugins that can write before this ordering boundary; consumer coexistence requires its own reviewed setup.

### Optional persistence facade probe

`-PersistenceProbe` requires Full and sets the save-data folder to the sandbox's `state` directory. It omits the API enable setting to exercise the enabled default; unselected persistence pilots explicitly opt out. Selection/root and a fresh completion receipt are checked by the launcher. Two synthetic providers exercise native capture/save/reload, mutation gates, provider removal and retained-intent reload refusal. Without additional consumer switches, real consumers may coexist but remain on their own persistence paths; this alone does not qualify their coordinated-storage migration.

`-JournalCoordinated` additionally requires a 0.3 or 0.4 MissionJournal candidate and `-PersistenceProbe`. It exercises default-enabled API-managed saves (no `UseApiSaveData` override) and explicitly enables read-only legacy import in the sandbox's journal config, pins that selection, requires an actual journal save/reload without an output legacy sidecar and a separate completion receipt, and checks that copied legacy journal files remain byte-identical with no additions. `-JournalMissionEventsProbe` additionally requires the 0.4 journal candidate and `-MissionIdentityProbe`. It selects API mission events, verifies direct journal hooks are absent, checks witnessed outcome projections (including neutral removal), and verifies saved acceptance history through repeated loads, failure-state advancement/rollback and save-as. Its marker, config selection and completion receipt are pinned. Stockpile still uses its legacy path in this mode unless separately selected with `-StockpileCoordinated`. This is controlled first-consumer adoption evidence, not complete recovery or two-consumer qualification.

`-StockpileCoordinated` additionally selects the 0.7 Stockpile candidate and requires the API-managed Journal pilot. It also omits the enable setting to exercise the default. It checks full transfer-queue JSON roundtrip without legacy output, reservation/fees/cancellation/delivery, shared storage refusal and retry, protected legacy import and provider teardown. The storage refusal temporarily replaces only the sandbox's `state` directory with a file, restoring it in `finally`; this probes failure to write an initial intent, not power-loss atomicity. A separate copied vanilla save and intentionally corrupt transfer sidecar exercise import refusal. Original copied transfer sidecars must remain unchanged; only that named refusal file may be added. Both actual-consumer receipts are mandatory. Owner acceptance and additional recovery scenarios remain separate.

`-ContentReferenceProbe` requires Full. It writes five disposable reference fixtures, reads their identities back and invokes the inspected native missing item/story-mission/patron/faction/POI lookup or factory boundary. Refusal and unchanged fixture text are required, with a fresh receipt. No foreign content is inserted into a vanilla save: this tests reconstruction boundaries, not an arbitrary mod uninstall or complete malformed-world load.

### API-absent gameplay control

`-VanillaLoadControl` is opt-in for `MissingApi`. After the menu, the API-independent guard loads both copied fixtures through vanilla GameManager, requires a new player and a live initialized GameplayManager, settles, cleans up the player, and returns through SceneLoader without the options-menu save action. Failure handling also attempts player/menu cleanup before quitting. The separate receipt records this current no-API comparison; it does not establish the cause of historical failed runs. No API lifecycle hooks or readiness events are involved. Selection and the successful completion receipt are checked independently of the guard version. Copied files are checked even without consumers. A read-only GetFreeOrbit exception finalizer records whether the world RNG was null, preserves the original exception, and does not turn a failed load into success.

### Actual private assembly-identity rejection

For `UnavailableApi` only, opt-in `-AssemblyOverlay` replaces the sandbox data junction with an owned data directory, copies Managed assemblies and top-level data files (including bundles/levels), and links the remaining resource directories read-only. Budget disk space for all copied data, not just the DLL. It appends a diagnostic PE overlay to the **private** Assembly-CSharp.dll without changing its IL; the original is hash-checked unchanged. The guard verifies that this private assembly really loaded and does not inject a hash result. The scenario must still refuse API capabilities/patches and selected consumers. This tests actual changed-file identity, **not compatibility with another game implementation/version**.

Provenance pins both hashes and the selection marker; Run also recomputes the hash of original bytes plus the diagnostic suffix before and after launch. The guard requires the original inspected identity and the specific hash-rejection capability reason. Cleanup unlinks only direct resource junctions and retains all copied files, including Managed; it refuses an unexpectedly linked overlay root. All copied/modified assemblies remain private and must never enter Git or releases.

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

With the same optional consumer selection, MissingApi requires BepInEx dependency refusal. UnavailableApi requires a present but disabled journal, no public facade and no journal-owned patches. It uses injected hash rejection by default; `-AssemblyOverlay` verifies actual changed private identity instead. Neither qualifies another game implementation. Prepared consumer markers and the scenario-specific flat plugin set are verified before launch.

## Authorized Stockpile pilot

Optional `-StockpileBin <owner-built Release/netstandard2.1 directory>` adds Stockpile0.6 (hard API0.1.1 dependency) and Newtonsoft. Both consumers may be selected; shared Newtonsoft bytes must agree. A sandbox-only config enables transfers. Existing transfer companions are copied; missing companions produce empty synthetic fixtures. Negative scenarios verify selection/refusal and the entire copied file set.

The full pilot pauses the transfer driver before gameplay to inspect copied queues. It then explicitly clears only the disposable in-memory queue, without refunding inventory, for controlled real station request/cancel/fee tests. It checks save/reload alignment, failed-save refusal, protected/corrupt sidecars, retry, slot replacement and component/UI/driver teardown. A manual near-completion tick is followed by actual Unity driver delivery. This is not pointer-driven transfer-dialog acceptance or uncontrolled natural-time testing. Original saves, inventories and credits are never write targets.

## Authorized Anima provider pilot

Add `-AnimaBin <Release directory> -AnimaRevision <exact 40-character commit>` to a Full preparation with `-MissionTransitionsProbe -MissionIdentityProbe -PersistenceProbe` and `-MissionJournalBin` (provides the agreed JSON runtime). Only the inspected Anima 0.3.0 / hard API 0.1.8 metadata shape is accepted before its startup sweeper can run. Existing paired Anima fixture sidecars are copied when present; originals remain protected. The profile explicitly disables LLM dispatch and leaves endpoint/key empty; it never copies private endpoint configuration.

The pilot uses the real assigner to construct a bounded gather-job blueprint, then validates native transitions and Anima's registry, repeated load/save-as, and rejection of stale-session publication. Its repeated-definition case uses an explicitly constructed fresh native occurrence backed by the owned blueprint. It injects a malformed observer callback deliberately to test the stop path (not a claim that the API emits malformed payloads), then verifies separate load-safety hook ownership, next-slot reconstruction cleanup, identity-preserving missing-definition string lookup, and refusal to publish changed in-memory data after stop.

`anima-missions.txt` and `anima-mission-events.tsv` are private receipts. Selection, source-revision label, binary hashes, disabled network configuration, required receipt and unused sandbox are verified. This does not qualify a real LLM service, all intents, or API-managed mission persistence: the current Anima observer integration retains legacy v4 sidecars and factory/lookup hooks. Automatic owner-scoped content persistence remains required by #13/#14 and the roadmap, not delivered by this probe. Native execution/evidence must be recorded separately from passing host/script checks.

## Remaining core-path probes

After consumer teardown, the full sequence attempts a normal in-system route, saves/reloads while travelling, then creates a controlled parked-space snapshot using vanilla cancellation/completion calls and verifies the empty Space load. A separate Unity-driven production observed iterator injects delayed adapter readiness/failure signals after a normal replacement load, then explicit disposal; it does not claim arbitrary asynchronous engine callback coverage. The direct adapter BeginLoad/EndLoadRequest pair adds a synthetic invalidation/start to the trace and must not be counted as a vanilla load. qa38 passed the strengthened probes; preserve the distinction between controlled fixture/injection coverage and general engine behavior.

Run results and source revisions belong in the pilot PRs. This tooling does not qualify all mission domain hooks, pre-readiness starter grants, TravelJournal coexistence or the rest of milestone 01.

## Native travel/station pilot (phased)

Add `-TravelStation` to a Full preparation. It appends `[Travel] Enabled=true` to `vgmodapi.cfg` (enabling the experimental `native-travel` capability and the public `ModApi.Travel`/`ModApi.Station` services), writes `travel-station.enabled`, and records the selection AND the reserved phase budget (`travelStationBudgetSeconds`) in provenance. The phase adds its own bounded waits on top of every existing Full pilot, so the run needs a longer process lifetime: `-TimeoutSeconds 3300` (base 1800 + phase 1500) or more. `Run` refuses a shorter lifetime BEFORE launching the game, the pilot publishes its own derived `budgetSeconds`, and a receipt whose budget exceeds the reservation is refused. The runner verifies the capability is available and that both public services are exposed, then subscribes through the PUBLIC event surfaces only to ASSERT outcomes while driving actual vanilla methods/Unity coroutines to DRIVE.

The delivered phase is `travel-in-system-station-v1` (one in-system round trip out of and back into the start station). Its required case identities are `initial-placement`, `station-undock`, `in-system-route`, `early-cancel`, `chained-route` and `station-dock`. The phase passes only when EVERY required case passed: a missing, not-run or failed required case is a FAIL, so empty or skipped coverage can never report PASS. Optional residual matrix cells are recorded separately as NOT-RUN rows and never count as coverage.

Each case owns a window: it captures the observed-fact offset immediately before it drives anything and asserts only within that window, so no earlier case's facts can satisfy a later wait or assertion. The pilot clears its buffers BEFORE the fixture load so the fresh session's own `InitialPlacement` is inside the first window, and it waits for the travel service to bind that session AND for the live local manager of the player's actual current POI to be initialized (`GameplayInitialized` alone is not treated as world readiness). A case window keeps facts of OTHER sessions instead of filtering them, so a stale-session fact leaking into a case is rejected by the validators rather than silently dropped; the load window is the single explicit boundary where the replaced session's facts are legitimate (they are counted in the receipt). Each wait has an explicit case-owned deadline; a timeout is a recorded failure, never a silent skip. Receipts (`travel-station.txt`, `travel-station-receipt.tsv` with session/operation identities and per-case evidence, `travel-station-events.tsv` with the API sequence, operation, origin/requested/actual, mode and game time of every observed fact) are CHECKPOINTED atomically after every case and written again on EVERY path, including an exception, which is recorded as a failed row for the case that was running plus `travel-station-fault.txt`. A checkpoint always says `INCOMPLETE`, never `PASS`, so an external termination can only leave incomplete evidence behind; the launcher additionally records `run-outcome.json` (timed out / killed / exit code) and a terminated run is refused even if a PASS receipt is on disk.

Cases legitimately OVERLAP in the native timeline: the return hop's dock is observed while the chained route is still driving. Each case therefore records explicit evidence references (`travel:5,6,7;station:3` — surface plus API sequence) for the facts it validated, and validation uses those references and the session identity, never a mutually exclusive case label. The `case` column of the event trace is only the observation context, and it is reset to `no-active-case` between cases so an optional residual cell can never be tagged onto a mandatory case's facts.

What each required case drives natively and asserts publicly:

- `initial-placement`: the fresh session's first public fact is `InitialPlacement` at the actual native location, with no fabricated arrival and no operation identity.
- `station-undock`: the player's own exit action (`SpaceStationInterior.ExitSpacestation` when the interior is open, otherwise `SpacestationExteriorManager.StartUndocking`) drives the real `DockingOption.Undock` coroutine to completion — the pilot never stops it — and asserts `Undocking` then `Leaving` for the docked station, native `DockingState.Leaving`, a released docking option and no travel fact.
- `in-system-route`: `TravelManager.TryInitiateTravel` to the nearest safe in-system POI (targets are selected nearest-first and exclude the current POI, hidden and dynamic POIs, jump gates, wormholes and other space stations, so the phase owns exactly one dock/undock pair; POI danger is not inspected because `totalEnemyCount` would force native content generation), asserting the exact ordered `Requested`->`Departed`->`Arrived`->`RouteCompleted` for one operation identity, plus native `IsLocalPoiReady`, the initialized target manager, the actual current POI and the public `CurrentLocation`.
- `early-cancel`: request, then `CancelTravel(null)` (bound by its exact `Nullable<Vector2>` signature) in the same frame, asserting `Requested`->`Cancelled` for one operation with the cancellation reported at the unchanged origin, no `Departed`, no `RecoveredPlacement` and cleared native waypoints.
- `chained-route`: a genuine two-hop native chain. Only the second waypoint is SET UP (appended to `GamePlayer.waypoints`, the same list a native multi-hop route fills); native `Travel()`/`TravelToNextWaypoint` remove the reached waypoint, report the arrival and start the next hop. Both hops must show their own ordered facts under distinct operation identities, with exactly one `RouteCompleted` at the final boundary. A single-leg fact stream cannot satisfy this case.
- `station-dock`: the return hop ends at the start station, where the native arrival path (`SpaceshipHasArrived` -> `CheckForDocking` -> `AssignClosestDockingOption` -> frames later `DockingOption.Update` -> `PerformDocking` -> `Dock()`) docks the ship. It asserts exactly one `DockedPhysical` for that station plus native `DockingState.Docked` and a docking option that holds the player ship. Interior readiness is recorded but deliberately NOT ordered against `DockedPhysical`: the arrival itself opens the interior synchronously through `CheckForSpaceStationEnter` (for any previously visited station), and `onDocked` also runs before the dock procedure ends, so both precede the physical completion.

Residual matrix cells recorded as optional NOT-RUN with their reason: `cross-system-jumpgate`, `cross-system-wormhole`, `empty-origin-reroute`, `restore-relink-dock` and `stale-session-replay`. They stay open work for later phases, not fulfilled scope.

`Assert-TravelStationReceipt` re-checks the outputs independently of the pilot's own claim: the launcher outcome (a timed-out, killed or non-zero-exit run is never a pass), the declared phase, the declared budget against the reservation, the exact required-case list, per-case passed status agreeing between summary and receipt, receipt/summary counts, a session identity per required case, evidence for every required case, and every evidence reference resolving to a real event of that case's session in the trace. A first line of `PASS` is never accepted on its own, and an `INCOMPLETE` checkpoint is refused. `TravelStationReceipt` (the pilot's PASS/FAIL and fact-ordering rules) is compiled into the host test project, so those rules have their own regressions, and the members and native drive paths the probe reflects are pinned by installed-assembly metadata tests.

This is controlled native evidence only, not owner acceptance: `RuntimeQualified=false` and #12 stays open until the owner qualifies in-game and the consumer milestones are reconciled. If the pilot exposes a genuine adapter defect in native flow, that is reported as a source-faithful finding for a follow-up fix (never patched via the pilot).
