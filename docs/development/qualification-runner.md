# Controlled Unity qualification runner

This development tooling is not part of the API distribution. Explicit authorization is required before test deployment or native execution. Full in-game acceptance remains a separate gate. Commands and case definitions describe current tooling; exact candidate receipts and execution reports stay outside this repository.

## Dungeon readiness probe (development)

`-Scenario Full -DungeonReadinessProbe` enables experimental boarding and authored content on an isolated copied-save fixture, without other probes or consumers. SaveA must be a copied save at an initialized POI (for example, docked at a station), with no travel or active boarding operation. Enabling authored content also activates native load-time recovery/resume hooks; this phase expects zero operations and cannot establish that loading arbitrary active-operation saves is read-only. It loads the fixture, requires published boarding/dungeon capabilities and services, and checks observed handles against the current lifecycle session. It deliberately does not open a panel, because native opening can resume operations or recover extraction. Zero observed targets is valid for this readiness-only phase, not evidence of encounter coverage.

Selection, configuration, process exit and a bounded hash-bound receipt are mandatory. Run `tools/tests/dungeon-readiness.Tests.ps1` for synthetic rejection checks. A passing readiness receipt does not qualify eligibility, commands, tactics, capture, rewards, returning crew, UI input, combined consumers or save/load recovery; those require their own native scenarios. No native result is implied by compilation or synthetic receipt validation.

`-DungeonPanelProbe` requires at least 900 seconds, the default windowed resolution and a settled POI with no other dungeon location appearing during the phase. It includes readiness and then constructs one scene-local native dungeon location using an installed definition. It does not add that target to save data or start combat. It registers a long status section and enabled/disabled actions, opens the native panel, sends a pointer click through a temporary Unity input device, checks close/reopen view identity, destroys its generated target and requires stale-target refusal. All contributor leases and the input device are released. A separate mandatory panel receipt prevents a readiness-only run from passing this phase. The private `dungeon-panel-actions.png` capture and matching SHA-256 record are mandatory and launcher-verified for existence, bounded size and integrity. The capture follows pointer readiness; inspect it rather than treating its presence as full visual acceptance. The v2 receipt requires real enabled/disabled pointer clicks and independent dispatch for two contributor identities. Cached UnityEvent callbacks (deliberately invoked directly, not presented as pointer input) test fresh eligibility revalidation, disposed-registration refusal and closed/previous-view refusal. Both identities are owned by the runner; this does not substitute for combined-consumer acceptance. This generated-location subset is not ship-panel, scaling, keyboard/controller, authored-choice, combat, settlement or recovery acceptance.

## Forge/refinery read probe (development)

`-Scenario Full -ForgeReadProbe` selects a read-only phase using a copied station fixture as SaveA, without other probes or consumers. It enables recipe integration only. The native driver compares public station/session/credits and restored-job counts with native state, enumerates supported/refused quotes, and verifies that synchronous reads do not change recipe pricing caches, credits or native job lists. A selected marker, recipe configuration, successful process exit and bounded hash-bound receipt are required by the launcher. Run `tools/tests/forge-reads.Tests.ps1` for synthetic selection/evidence checks without Unity.

`-BlueprintPinProbe -BlueprintPinBin <directory> -BlueprintPinRevision <40-hex-commit> -BlueprintPinSha256 <64-hex-digest>` selects a separate read-plus-consumer phase. Supply a reviewed Blueprint Pin 0.2.0 build with its public API dependency; the launcher checks assembly dependencies and binary identity, copies only that DLL, and enables recipe/HUD integration in the sandbox. The probe drives the real Pin action, checks a one-batch target, navigates away and back through the HUD to the exact variant, and closes the panel. It also finds a single Forge producer and navigates through an ingredient click, then checks an alternative-producer chooser, route-specific enabled states, a disabled non-Forge-row pointer click after scrolling to the final row, and backing out without clearing the pin. Fixture setup selects one batch through the native slider; native recipe selection otherwise defaults to the maximum craftable quantity. Both screenshots are hash-bound. The fixture must contain eligible inputs (up to seven ingredient rows, two to 32 alternative routes ending in a non-Forge route). This does not qualify activating a producer from the chooser, multi-output counts, job progress, reload or all consumer coexistence. Other probes and consumers are excluded.

`-ForgeUiProbe` includes the read phase and excludes command phases. It requires a selectable station recipe with alternate variants, registers two public actions, drives Unity Input System pointer events through rendered buttons, verifies disabled and stale-press refusal, exact-variant selection, close/reopen view replacement and registration disposal. It waits for rendered graphics, verifies that the owned strip does not overlap native recipe content or facility tabs on both openings, and captures hash-bound private screenshots at original and 1.25 canvas scale. It verifies pointer input at 1.25 scale, forces a temporarily unusable 20x canvas to verify safe hiding, restores the original scaler in `finally`, and checks registration/input recovery. This is controlled input coverage, not human acceptance, Blueprint Pin qualification, all-resolution coverage or full consumer coexistence.

`-ForgeCommandProbe` additionally enables crafting commands and includes the read phase. It verifies setting toggles against native fields, request conflicts, replay without reapplying restored settings, a two-batch Forge queue with native batch count/credit conversion and event receipts, queue replay without a second debit, and immediate cancellation of the owned job. It temporarily sets copied-player credits to zero (restored in `finally`), verifies public and direct native queue refusal without input/job mutation or an admitted-queue event, then observes an affordable direct native start and cancels it through the public API. It fills one to eight free Forge slots using real queue commands, verifies public and native guarded-start capacity refusal without mutation, and cancels its setup jobs to restore the original job list. If the copied fixture's Forge is full, it deliberately cancels one copied job through the API as setup. A separate mandatory completion receipt distinguishes this subset from read-only runs. The fixture must provide sufficient inputs and credits; unavailable setup fails rather than skipping the case. It does not qualify partial completion, inventory/refund economics, refinery output, extraction, persistence or consumer UI.

`-ForgePersistenceProbe` includes both preceding phases, then pauses only native Forge/refinery `ProgressJobs` with scoped Harmony prefixes while testing nonempty native job save/reload, save-as and slot switching. It requires matching job multisets, stale runtime handle refusal, no synthetic queue replay on restoration, and persistence of station auto-refine, player cargo delivery and stored/effective auto-sell. The prefixes are removed in `finally`. A separate mandatory receipt identifies this paused-progression subset. This does not establish natural completion timing, delivered outputs, interrupted writes, failed-load recovery or missing-content safety.

`-ForgeDeliveryProbe` includes read/command phases but excludes the paused persistence phase. It explicitly drives real native `ProgressJob` delta time: one batch then partial cancellation, and two batches in one call followed by native finished-job retirement. Delivery receipts are compared with independent inventory deltas by destination, resource identifier, level and rarity. Partial refunds additionally match the native one-remaining-batch ingredient getters, float material accumulation, station item routing and current credit price. The selected fixture must have a supported affordable recipe and free slot after command setup. A separate mandatory receipt identifies this driven subset; it is not wall-clock timing, all recipe variants, deterministic bonus coverage, refinery/extraction or full UI qualification.

`-RefineryProbe` includes read/command phases and excludes delivery/persistence selections. It requires an affordable fractional-yield ore fixture, drives one real native refinement batch, checks native float yields (including only eligible bonus outcomes) and per-resource transfer receipts, then cancels the remaining ore with native ore/credit refund checks. After cancellation it temporarily marks exact remaining ore rows as favourites, checks refusal under both favourite-protecting policies without input/job/credit mutation, and restores every copied flag in `finally`. This does not exercise the distinct mission-item predicate or native-consumption policy on favourite rows. It then requires two further affordable fractional ore batches, drives both in one native progress call, verifies ordered batch facts against possible native yield sequences and measured deliveries, and checks exactly one retirement. It also extracts one available material into its native cargo canister, checks material/credit debit and receipt, and rejects repeated replay effects. A full copied refinery may free one slot through actual cancellation as setup. Separate selection and completion evidence are mandatory. This does not force a bonus occurrence, qualify every ore/route, rejection policy, failure recovery or UI coexistence.

The read-only phase does **not** qualify queue/cancel/extraction mutations, all inventory economics, save recovery, UI input/scaling or consumer coexistence. Passing it cannot close the full Forge/refinery acceptance matrix. Native receipts and source findings remain private; the phase must actually execute before its behavior is claimed as qualified.

## Offline Mods screen input probe (development)

Prepare with `-Scenario Full -ModMenuProbe`, without consumer DLLs or other probe switches. It runs only the native menu input probe instead of loading fixtures. The preparation marker is validated against provenance before launch; a bounded SHA-256 receipt is accepted only after a valid recorded process exit. A source review does not grant a native lease: coordinate exclusive ownership before Prepare/Run.

The driver queues temporary Unity Input System keyboard/mouse devices through the actual UI input module (not physical hardware). It tests submit, long-description Up/Down scrolling, scrollbar geometry, Tab, pointer Close, Escape/focus restoration, a raycast barrier, inactive-menu cleanup and reattachment. Only its own sandbox metadata sidecar is created, then removed. The v3 probe exercises a native confirmation modal, temporary canvas replacement/return, API shutdown, and immediate manual update refresh without confirmation or an automatic-check toggle. Automatic checks may run; this UI probe alone does not qualify TLS. It captures private, hash-bound screenshots at the original resolution, 1280×720, and the update status, restoring the original resolution afterward. All three files are mandatory and digest-validated; older receipt versions are rejected. Human inspection of those captures remains necessary for readability. The receipt does **not** establish physical gamepad behavior, browser opening or full in-game acceptance. Readability requires human inspection even when input and lifecycle assertions pass. Private screenshots and receipts are retained outside the repository, not distributed. Full UI qualification remains pending.

## Native update transport probe (development)

Generate a private throwaway fixture with `python3 tools/make_test_certificate.py --output <new-private-file.pfx>` (OpenSSL required); never import it into a trust store or distribute it. Add `-ModInformationProbe -TlsFixture <file.pfx> -MissionJournalBin <directory> -StockpileBin <directory>` to `-Scenario Full -ModMenuProbe`, with both real consumer builds. Reserve at least 900 seconds. After preparation, start `tools/qualification-browser.ps1 -SandboxRoot <root> -TimeoutSeconds 900` alongside the owned Run process and require both processes to succeed. This opt-in extension runs the production checker on Unity with controlled transport faults, scheduling, disk-cache and cancellation assertions, then fetches synthetic fixtures from the exact build revision in the public API repository. The real platform transport exercises TLS, a GitHub HTTPS redirect, both channels, and invalid/oversized responses; the build revision must already be pushed. Controlled service DNS/TLS/timeout exceptions remain labeled as injection. Separately, the probe exercises native name-resolution failure on the reserved `.invalid` domain, certificate rejection against its own ephemeral loopback TLS server, and cancellation of a real stalled handshake. These call the transport primitive only: production feed-host policy and platform certificate validation are not weakened. The private test certificate is hash-bound to preparation, loaded with ephemeral key storage and never trusted. All sockets/tasks are bounded and tied to probe teardown. No production release is announced or installed. V3 also exercises two real consumers, malformed/wrong-GUID metadata presentation, gameplay load/return and new-game save/load, real Stockpile UI coexistence, immediate manual refresh and automatic checking without a toggle, temporary UI scaling, and the actual adapter-unavailable path before shutdown. Its launcher observer wraps and calls the original Unity browser launcher, rejecting any invocation before the explicit click. The external observer verifies the foreground Chrome release page and exact HTTPS address through UI Automation, captures only its verified page-content region (excluding browser tabs/toolbars/profile controls and the website's top account bar), and restores owned-game focus; it never closes a pre-existing browser. The three ordinary menu images, scale/unavailable images, browser image/URI receipt, and every fact are mandatory. Earlier information receipt versions cannot attest the full scenario. These are bounded acceptance cases, not universal game, browser, physical-input or save-compatibility qualification.

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

The solution builds `QualificationRunner.dll` and `QualificationGuard.dll`, but the API package allowlist excludes both. On Windows, run the self-authored PowerShell script from a **local** path if RemoteSigned treats a WSL UNC path as remote; do not disable machine execution policy. Keep `qualification-profile.ps1`, `qualification-inputs.ps1` and `qualification-story.ps1` beside the launcher; copy the test scripts with their relative directory layout.

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

`-PersistenceProbe` requires Full and selects the persistence test cases. Every preparation sets the API save-data folder to the sandbox's `state` directory, whether this probe is selected or not; API save data has no enable switch. Selection/root and a fresh completion receipt are checked by the launcher. Two synthetic providers exercise native capture/save/reload, mutation gates, provider removal and retained-intent reload refusal. Without additional consumer switches, real consumers may coexist but remain on their own persistence paths; this alone does not qualify their coordinated-storage migration.

`-JournalCoordinated` additionally requires a 0.3 or 0.4 MissionJournal candidate and `-PersistenceProbe`. It exercises default-enabled API-managed saves (no `UseApiSaveData` override) and explicitly enables read-only legacy import in the sandbox's journal config, pins that selection, requires an actual journal save/reload without an output legacy sidecar and a separate completion receipt, and checks that copied legacy journal files remain byte-identical with no additions. `-JournalMissionEventsProbe` additionally requires the 0.4 journal candidate and `-MissionIdentityProbe`. It selects API mission events, verifies direct journal hooks are absent, checks witnessed outcome projections (including neutral removal), and verifies saved acceptance history through repeated loads, failure-state advancement/rollback and save-as. Its marker, config selection and completion receipt are pinned. Stockpile still uses its legacy path in this mode unless separately selected with `-StockpileCoordinated`. This probes the selected journal paths, not complete recovery or two-consumer qualification.

`-StockpileCoordinated` additionally selects the 0.7 Stockpile candidate and requires the API-managed Journal pilot. It also omits the enable setting to exercise the default. It checks full transfer-queue JSON roundtrip without legacy output, reservation/fees/cancellation/delivery, shared storage refusal and retry, protected legacy import and provider teardown. The storage refusal temporarily replaces only the sandbox's `state` directory with a file, restoring it in `finally`; this probes failure to write an initial intent, not power-loss atomicity. A separate copied vanilla save and intentionally corrupt transfer sidecar exercise import refusal. Original copied transfer sidecars must remain unchanged; only that named refusal file may be added. Both actual-consumer receipts are mandatory. Full in-game acceptance and additional recovery scenarios remain separate.

`-ContentReferenceProbe` requires Full. It writes five disposable reference fixtures, reads their identities back and invokes the inspected native missing item/story-mission/patron/faction/POI lookup or factory boundary. Refusal and unchanged fixture text are required, with a fresh receipt. No foreign content is inserted into a vanilla save: this tests reconstruction boundaries, not an arbitrary mod uninstall or complete malformed-world load.

### API-absent gameplay control

`-VanillaLoadControl` is opt-in for `MissingApi`. After the menu, the API-independent guard loads both copied fixtures through vanilla GameManager, requires a new player and a live initialized GameplayManager, settles, cleans up the player, and returns through SceneLoader without the options-menu save action. Failure handling also attempts player/menu cleanup before quitting. The separate receipt records only this no-API comparison; it does not attribute failures in other configurations. No API lifecycle hooks or readiness events are involved. Selection and the successful completion receipt are checked independently of the guard version. Copied files are checked even without consumers. A read-only GetFreeOrbit exception finalizer records whether the world RNG was null, preserves the original exception, and does not turn a failed load into success.

### Actual private assembly-identity rejection

For `UnavailableApi` only, opt-in `-AssemblyOverlay` replaces the sandbox data junction with an owned data directory, copies Managed assemblies and top-level data files (including bundles/levels), and links the remaining resource directories read-only. Budget disk space for all copied data, not just the DLL. It appends a diagnostic PE overlay to the **private** Assembly-CSharp.dll without changing its IL; the original is hash-checked unchanged. The guard verifies that this private assembly really loaded and does not inject a hash result. The scenario must still refuse API capabilities/patches and selected consumers. This tests actual changed-file identity, **not compatibility with another game implementation/version**.

Provenance pins both hashes and the selection marker; Run also recomputes the hash of original bytes plus the diagnostic suffix before and after launch. The guard requires the original inspected identity and the specific hash-rejection capability reason. Cleanup unlinks only direct resource junctions and retains all copied files, including Managed; it refuses an unexpectedly linked overlay root. All copied/modified assemblies remain private and must never enter Git or releases.

## Evidence and coverage

Local output includes `events.tsv` (with failure detail/exception type when supplied by the API), `result.txt`, game/BepInEx logs, process/provenance receipts, and preservation markers. A PASS result is meaningful only together with successful save/prefs preservation and log inspection. A run-start receipt or pre-existing preference snapshot prevents retry from overwriting recovery evidence; restore failures get a separate private receipt even if save verification also fails. **Treat every raw artifact as private**, including TSV details, stack traces, receipts, and registry snapshots: they may contain usernames, full paths, or preferences. Publish only reviewed, redacted excerpts.

The automated sequence checks copied loads, replacement, manual save/roundtrip, ephemeral skip, exhausted retries, subscribers and rejection/recovery. The extended sequence adds four calls through vanilla's autosave-slot selector, one narrowly scoped metadata-write exception to exercise successful retry, and equal empty-player fixtures with newer/current headers. It drives the native new-game wizard callbacks, observes its synchronous configuration boundary, and attempts a fresh space-save roundtrip. These are scripted callback checks, not pointer-driven UI acceptance; consult the recorded results before treating a new scenario as exercised. It uses vanilla GameManager.LoadGame rather than assuming an iterator factory completing means readiness. Session-id checks prevent stale-session acceptance; an unscaled two-second settle grace permits remaining startup work and time scale is not frozen for saving. This is a harness heuristic, not a universal world/UI-ready guarantee.

The modal probe maps the inspected original `Behaviour.UI.AlertPopup.ShowMessage(string, string, Action)` method, private static `activeInstance`, static `IsOpen` getter, and private `submitButton` field. `CreateButton` registers `DestroyPopup` then the confirmation callback on `submitButton.onClick`; destruction clears `activeInstance` and unpauses. Reinspect the installed assembly at the recorded hash rather than relying on older workspace decompilations, which may lack these members.

Expected rejection popups must be acknowledged before proceeding: observe the scoped `ShowMessage` key, invoke only that popup's actual confirmation button (including vanilla callbacks), and wait for destruction/menu cleanup. Do not dismiss unknown dialogs or advance the new-game wizard behind a modal.

A failed check must remain failed until its cause is investigated. Record bootstrap/harness failures separately from reproduced API defects. No test edits the allowed game hash or claims all-world readiness.

Run `tools/tests/qualification.Tests.ps1` on Windows for synthetic-file tests of manifest coverage, the DLL allowlist, preloader configuration, provenance, path/reuse refusal, and junction cleanup. These tests do not launch Unity. `tools/tests/qualification-profile.Tests.ps1` separately exercises snapshot/restore and backup verification using a unique synthetic registry key, never the real game's preferences. Read-only startup diagnostics require the inspected synchronous `void GameplayManager.Start` signature and can be enabled with `-Diagnostics` on Run. The valid-syntax newer-version fixture must end without readiness, not merely throw a parser error. Its equal empty-player current-header control exercises nested deserialization failure; public events alone do not uniquely identify the too-new-version branch.

Save hashes do not prove unchanged preferences: both require independent preservation checks. Controlled coverage and current limits are summarized in [compatibility](../reference/compatibility.md). Pointer-driven UI acceptance, arbitrary async engine callbacks and actual alternate game binaries remain separate; injected hash rejection is not alternate-version compatibility. The inspected autosave selector chooses the first missing slot, then the oldest file by modification time; the four-call 0/1/2/0 expectation assumes distinct timestamps on the tested filesystem and may fail on coarse-resolution filesystems. Do not set RuntimeQualified merely because the automated subset passes.

## Authorized MissionJournal pilot

Prepare accepts optional `-MissionJournalBin <locally-built Release/netstandard2.1 directory>`. Only VGMissionJournal.dll and Newtonsoft.Json.dll are added; never copy installed legacy plugins blindly. Cecil verifies journal identity/version 0.2 and the hard `vgmodapi` version constructor before launch, before allowing the candidate's startup sweeper to run. This metadata check is not review or binary provenance evidence. Keep the exact hashes and source revision in the private prepared manifest.

All consumer scenarios require existing companion journal sidecars for both supplied saves and copy them into the sandbox. Negative startup runs hash-check the complete sandbox save-file set after exit to verify that disabled consumers did not touch those sidecars. Reflection-only probes compare nonempty persisted history IDs with the public facade after slot switching/reload, reject prior-slot-only history, compare successful destination sidecars with live history, and reject failed/skipped-save writes. New game must not inherit old history; destroying only the journal component must unsubscribe and prevent subsequent sidecar writes. No real save/sidecar is a write target. Counts/IDs are not exported publicly.

With the same optional consumer selection, MissingApi requires BepInEx dependency refusal. UnavailableApi requires a present but disabled journal, no public facade and no journal-owned patches. It uses injected hash rejection by default; `-AssemblyOverlay` verifies actual changed private identity instead. Neither qualifies another game implementation. Prepared consumer markers and the scenario-specific flat plugin set are verified before launch.

## Authorized Stockpile pilot

Optional `-StockpileBin <locally-built Release/netstandard2.1 directory>` adds Stockpile0.6 (hard API0.1.1 dependency) and Newtonsoft. Both consumers may be selected; shared Newtonsoft bytes must agree. A sandbox-only config enables transfers. Existing transfer companions are copied; missing companions produce empty synthetic fixtures. Negative scenarios verify selection/refusal and the entire copied file set.

The full pilot pauses the transfer driver before gameplay to inspect copied queues. It then explicitly clears only the disposable in-memory queue, without refunding inventory, for controlled real station request/cancel/fee tests. It checks save/reload alignment, failed-save refusal, protected/corrupt sidecars, retry, slot replacement and component/UI/driver teardown. A manual near-completion tick is followed by actual Unity driver delivery. This is not pointer-driven transfer-dialog acceptance or uncontrolled natural-time testing. Original saves, inventories and credits are never write targets.

## Authorized Anima provider pilot

Add `-AnimaBin <Release directory> -AnimaRevision <exact 40-character commit>` to a Full preparation with `-MissionTransitionsProbe -MissionIdentityProbe -PersistenceProbe` and `-MissionJournalBin` (provides the agreed JSON runtime). Only the inspected Anima 0.3.0 / hard API 0.1.8 metadata shape is accepted before its startup sweeper can run. Existing paired Anima fixture sidecars are copied when present; originals remain protected. The profile explicitly disables LLM dispatch and leaves endpoint/key empty; it never copies private endpoint configuration.

The pilot uses the real assigner to construct a bounded gather-job blueprint, then validates native transitions and Anima's registry, repeated load/save-as, and rejection of stale-session publication. Its repeated-definition case uses an explicitly constructed fresh native occurrence backed by the owned blueprint. It injects a malformed observer callback deliberately to test the stop path (not a claim that the API emits malformed payloads), then verifies separate load-safety hook ownership, next-slot reconstruction cleanup, identity-preserving missing-definition string lookup, and refusal to publish changed in-memory data after stop.

`anima-missions.txt` and `anima-mission-events.tsv` are private receipts. Selection, source-revision label, binary hashes, disabled network configuration, required receipt and unused sandbox are verified. This does not qualify a real LLM service, all intents, or API-managed mission persistence: the current Anima observer integration retains legacy v4 sidecars and factory/lookup hooks. Automatic persistence of API-owned content is a separate story-service contract, not provided by this observer probe. Native execution/evidence must be recorded separately from passing host/script checks.

## Remaining core-path probes

After consumer teardown, the full sequence attempts a normal in-system route, saves/reloads while travelling, then creates a controlled parked-space snapshot using vanilla cancellation/completion calls and verifies the empty Space load. A separate Unity-driven production observed iterator injects delayed adapter readiness/failure signals after a normal replacement load, then explicit disposal; it does not claim arbitrary asynchronous engine callback coverage. The direct adapter BeginLoad/EndLoadRequest pair adds a synthetic invalidation/start to the trace and must not be counted as a vanilla load. Controlled fixture/injection coverage does not establish general engine behavior.

Run results and source revisions belong in the pilot PRs. This sequence does not qualify all mission domain hooks, pre-readiness starter grants or TravelJournal coexistence.

## Native travel/station pilot (phased)

Add `-TravelStation` to a Full preparation. The travel API initializes automatically; the switch selects test cases, writes `travel-station.enabled`, and records the selection AND the reserved phase budget (`travelStationBudgetSeconds`) in provenance. The phase adds its own bounded waits on top of every existing Full pilot, so the run needs a longer process lifetime: `-TimeoutSeconds 3300` (base 1800 + phase 1500) or more. The published `budgetSeconds` is SUMMED from the same per-wait deadline constants the driver's waits use, so changing a deadline moves the budget, and the pilot refuses to run if the shared harness wait/settle deadlines or the launcher reservation no longer cover it. `Run` refuses a shorter lifetime BEFORE launching the game, the pilot publishes its own derived `budgetSeconds`, and a receipt whose budget exceeds the reservation is refused. The runner verifies the capability is available and that both public services are exposed, then subscribes through the PUBLIC event surfaces only to ASSERT outcomes while driving actual vanilla methods/Unity coroutines to DRIVE. If the native travel group failed to install, the capability is unavailable and the services are absent, so the phase fails closed with its receipt, event trace and `travel-station-fault.txt` preserved rather than reporting success. See [binding constraints](../reference/compatibility.md).

The phase is `travel-in-system-station-v1` (one in-system round trip out of and back into the start station). Its required case identities are `initial-placement`, `station-undock`, `in-system-route`, `early-cancel`, `chained-route` and `station-dock`. The phase passes only when EVERY required case passed: a missing, not-run or failed required case is a FAIL, so empty or skipped coverage can never report PASS. Optional residual matrix cells are recorded separately as NOT-RUN rows and never count as coverage.

Each case owns a window: it captures the observed-fact offset immediately before it drives anything and asserts only within that window, so no earlier case's facts can satisfy a later wait or assertion. The pilot clears its buffers BEFORE the fixture load so the fresh session's own `InitialPlacement` is inside the first window, and it waits for the travel service to bind that session AND for the live local manager of the player's actual current POI to be initialized (`GameplayInitialized` alone is not treated as world readiness). A case window keeps facts of OTHER sessions instead of filtering them, so a stale-session fact leaking into a case is rejected by the validators rather than silently dropped; the load window is the single explicit boundary where the replaced session's facts are legitimate, and only as a contiguous prefix before the fresh session's first fact (they are counted in the receipt; a replaced-session fact interleaved after that boundary is rejected). Each wait has an explicit case-owned deadline; a timeout is a recorded failure, never a silent skip. Receipts (`travel-station.txt`, `travel-station-receipt.tsv` with session/operation identities and per-case evidence, `travel-station-events.tsv` with the API sequence, operation, origin/requested/actual, mode and game time of every observed fact) are CHECKPOINTED atomically after every case and written again on EVERY path, including an exception, which is recorded as a failed row for the case that was running plus `travel-station-fault.txt`. A checkpoint always says `INCOMPLETE`, never `PASS`, so an external termination can only leave incomplete evidence behind; the launcher additionally records `run-outcome.json` (timed out / killed / exit code / self-terminated) and a terminated run is refused even if a PASS receipt is on disk. The exit-code gate follows the game's own quit contract: `ApplicationQuitHandler.OnApplicationQuit` autosaves and then calls `Process.GetCurrentProcess().Kill()`, which the shipped Mono `System.dll` implements as `TerminateProcess(handle, -1)`, so a normal quit always reports `-1` and the `Application.Quit(...)` argument never reaches the OS. Only a clean `0` and that source-proven `-1` are accepted; any other or unknown exit code still refuses the run (see [compatibility](../reference/compatibility.md)).

Cases legitimately OVERLAP in the native timeline: the return hop's dock is observed while the chained route is still driving. Each case therefore records explicit evidence references (`travel:5,6,7;station:3` — surface plus API sequence) for the facts it validated, and validation uses those references and the session identity, never a mutually exclusive case label. The `case` column of the event trace is only the observation context, and it is reset to `no-active-case` between cases so an optional residual cell can never be tagged onto a mandatory case's facts.

What each required case drives natively and asserts publicly:

- `initial-placement`: the fresh session's first public fact is `InitialPlacement` at the actual native location, with no fabricated arrival and no operation identity.
- `station-undock`: the player's own exit action (`SpaceStationInterior.ExitSpacestation` when the interior is open, otherwise `SpacestationExteriorManager.StartUndocking`) drives the real `DockingOption.Undock` coroutine to completion — the pilot never stops it — and asserts `Undocking` then `Leaving` for the docked station, native `DockingState.Leaving`, a released docking option and no travel fact.
- `in-system-route`: `TravelManager.TryInitiateTravel` to the nearest safe in-system POI, asserting the exact ordered `Requested`->`Departed`->`Arrived`->`RouteCompleted` for one operation identity, plus native `IsLocalPoiReady`, the initialized target manager, the actual current POI and the public `CurrentLocation`.
- `early-cancel`: request, then `CancelTravel(null)` (bound by its exact `Nullable<Vector2>` signature) in the same frame, asserting `Requested`->`Cancelled` for one operation with the cancellation reported at the unchanged origin, no `Departed`, no `RecoveredPlacement` and cleared native waypoints.
- `chained-route`: a genuine two-hop native chain. Only the second waypoint is SET UP (appended to `GamePlayer.waypoints`, the same list a native multi-hop route fills); native `Travel()`/`TravelToNextWaypoint` remove the reached waypoint, report the arrival and start the next hop. Both hops must show their own ordered facts under distinct operation identities, with exactly one `RouteCompleted` at the final boundary. A single-leg fact stream cannot satisfy this case.
- `station-dock`: the return hop ends at the start station, where the native arrival path (`SpaceshipHasArrived` -> `CheckForDocking` -> `AssignClosestDockingOption` -> frames later `DockingOption.Update` -> `PerformDocking` -> `Dock()`) docks the ship. It asserts exactly one `DockedPhysical` for that station plus native `DockingState.Docked` and a docking option that holds the player ship. Interior readiness is recorded but deliberately NOT ordered against `DockedPhysical`: the arrival itself opens the interior synchronously through `CheckForSpaceStationEnter` (for any previously visited station), and `onDocked` also runs before the dock procedure ends, so both precede the physical completion.

All in-system targets come from ONE shared authoritative selector (`Plugin.SafeInSystemTargets`), nearest-first: an allowlist of the two industrial POI kinds (`Source.Galaxy.POI.Mining`, `Source.Galaxy.POI.Salvage`) that additionally excludes the current POI, hidden and dynamic POIs, native combat encounters (`Combat` and its `CombatStation`/`Escort`/`LureSite` subclasses), other space stations (so the phase owns exactly one dock/undock pair), jump gates, wormholes, sites owned by a faction the game reports hostile to the player, story-mission locations, and any POI whose persisted `guardDescriptors` list is non-empty (that protected list is exactly what `MapPointOfInterest.RegenerateGuardUnits` spawns from, and it is read by COUNT only). The guard rule closes the one shape the type allowlist alone admits: a mission-generated Mining POI such as `MiningDeadDrop.SetupPOI` is a plain `Source.Galaxy.POI.Mining` with a NEUTRAL mission faction, no `storyId` and no dynamic flag, whose guards are nevertheless marked player-hostile. `MapPointOfInterest.activeEnemyCount`/`totalEnemyCount` are still never read, because their getters call `EnsureContentGenerated` and would generate native content as a side effect of observation. The decision itself is the pure, host-tested rule `TravelStationReceipt.RefuseTravelTarget`, and a NOT-RUN row records the per-reason refusal counts. This selection REDUCES the chance of native hostility; it cannot prove a POI is safe (live units, storyteller payloads and wandering hostiles are not visible to it). Hull destruction can trigger vanilla's emergency jump (`SpaceShip.TryEmergencyJump` -> `TravelManager.TravelToClosestSpacestation`) and an unsolicited return route. Independently of the selection, every case opens its quiet window BEFORE its travel-availability wait and refuses to start unless the native travel surface is silent: an unsolicited public fact or an already-active native route fails the case with the observed fact and a read-only native autonomy diagnostic (emergency-jump and autopilot flags, hull/shield, current POI, waypoints, target/local target, warping, travel-active). An unexpected native route is never waited out, filtered away or replaced by another target.

Residual matrix cells recorded as optional NOT-RUN with their reason: `cross-system-jumpgate`, `cross-system-wormhole`, `empty-origin-reroute`, `restore-relink-dock` and `stale-session-replay`. They are outside this phase's scope. Selecting the separate cross-system or resilience phase below does **not** change these rows: this phase's coverage is still its own six required cases, and each other phase is validated on its own receipt.

`Assert-TravelStationReceipt` re-checks the outputs independently of the pilot's own claim: the launcher outcome (a timed-out, launcher-killed or disallowed/unknown-exit run is never a pass), the declared phase, the declared budget against the reservation, the exact required-case list, per-case passed status agreeing between summary and receipt, receipt/summary counts, a session identity per required case, evidence for every required case, and every evidence reference resolving to a real event of that case's session in the trace. A first line of `PASS` is never accepted on its own, and an `INCOMPLETE` checkpoint is refused. `TravelStationReceipt` (the pilot's PASS/FAIL and fact-ordering rules) is compiled into the host test project, so those rules have their own regressions, and the members and native drive paths the probe reflects are pinned by installed-assembly metadata tests.

A passing phase supplies bounded controlled native evidence, not full in-game acceptance: `RuntimeQualified=false`. If the pilot exposes a genuine adapter defect in native flow, that is reported as a source-faithful finding for a follow-up fix (never patched via the pilot).

## Native cross-system travel phase (separate, optional)

`-TravelCrossSystem` is an ADDITIONAL Prepare selection that requires `-TravelStation` (it reuses the same native travel service), writes `travel-cross-system.enabled`, and records its own reservation (`travelCrossSystemBudgetSeconds`) in provenance. It runs as its own phase `travel-cross-system-v1` with its own receipts (`travel-cross-system.txt`, `travel-cross-system-receipt.tsv`, `travel-cross-system-events.tsv`, `travel-cross-system-fault.txt`) and its own mandatory case identities: `cross-system-jumpgate` and `cross-system-wormhole`. It never widens the in-system phase, whose optional cross-system rows stay NOT-RUN.

Budgets add up rather than replace: `Run` refuses to launch unless `-TimeoutSeconds` is at least base 1800 + travel/station 1500 + cross-system 2400 (**5700**) when both phases are selected. As in the in-system phase, the published `budgetSeconds` is SUMMED from the same per-wait deadline constants the driver uses, the pilot refuses to run when the shared harness wait/settle deadlines or the launcher reservation no longer cover it, and a receipt whose budget exceeds the reservation is refused. The wait plan is DERIVED from the number of required cases and from the per-case call-site multiplicities (two fixture-load/binding waits, one undock, one availability sample, one approach arrival, one handoff, one jump arrival, **two route boundaries** — the approach boundary and the cross-system boundary — and three settles per case, plus the opt-in fixture creation and the restoring load with their settles), so the bound accounts for each declared wait occurrence. The published worst case is 2184 seconds against the 2400 reservation.

Each case starts from its own freshly loaded `fixture-a` session (the cases legitimately end in another system, so a reused world would leave the next case's start undefined), drives the player's own station exit, and then performs a two-step player route.

Because that load DESTROYS the previous scene's manager, the case captures its native owner — the exact `TravelManager` instance and player — only at its own load/readiness boundary, never earlier, and never re-binds it: a Unity-destroyed instance keeps a live-looking managed reference, and driving it reaches `MonoBehaviour.StartCoroutine` on a dead behaviour even though its managed reference is non-null. Every native drive and every observation snapshot first proves that the captured owner is still the live current one in the same session; a destroyed, replaced or foreign-session owner is a recorded failure with identity/liveness/session diagnostics, never a re-bind and never a native call. Cross-system `Departed`/`Arrived` facts additionally have to be sampled while the world is still that owner.

The two-step player route is:

1. An in-system approach to the source gate/wormhole. The native arrival auto-handoff (`JumpGateManager.SpaceshipHasArrived` -> `InitiateTravelThroughGate`, `WormholeManager.SpaceshipHasArrived` -> `InitiateTravelThroughWormhole`) is a NO-OP there, because `Travel()` removes the reached waypoint before it runs, so the ship parks at the gate/wormhole.
2. The actual cross-system request from it. `StartTravel` recognises the current gate/wormhole, hands off to `InitiateTravelThroughGate`/`InitiateTravelThroughWormhole`, and the native `TheGate`/`TheWormhole` objects move the ship, cross the threshold and start `JumpToSystem`/`JumpToWormhole` themselves. The pilot never starts, advances or completes a jump iterator, never assigns a location and never teleports.

Selection uses the read-only native planner `TravelManager.GenerateShortestRoute`, so the hop is chosen without driving anything and without assuming which gate the planner prefers: a candidate is used only when the planner really plans that single hop. The one-way tutorial exit gate (`Hermetis` -> `Canis Majoris`, which calls `TransitionTutorialToSandbox`) is excluded by reading the stored `MapElement._name` field, so the lazy name getter cannot generate names or consume world randomness.

The asserted evidence for each case is the exact public fact stream of both routes — `Requested`->`Departed`->`Arrived` per leg plus one `RouteCompleted` per route, distinct operation identities, per-leg mode, and locations compared against the loaded world — plus:

- the cross leg's `RequestedDestination` is the RAW `targetSystemGuid`/`targetPoiGuid` of the gate (or the waypoint wormhole), captured before the jump can rewrite anything; a redirect is recorded, and an arrival that lost its raw request is a failure;
- the final `RouteCompleted` belongs to the cross-system leg's operation and mode, so it can only come from the native final chain;
- the cross leg's `Departed`/`Arrived` were sampled while the native jump iterator was running (`usingJumpgate`), at the mode's own live initialized manager (`JumpGateManager`/`WormholeManager`), agreeing with the loaded world's system/POI. Because the inspected `JumpToSystem`, `JumpToWormhole` and `TravelToWormholeDestination` never call `SpaceshipHasArrived` (pinned by an installed-assembly test), this is what proves the arrival came from the owned iterator observation and not from the in-system arrival path. No in-system fact of the case may be sampled inside a jump.

The read-only native snapshots record only system/POI identity, jump/travel state and the live manager; a loaded POI and empty space are both supported and ship positions are deliberately never read, so a direct pointer/teleport can never be mistaken for a transition. Receipts are checkpointed atomically after every case and written on every path (an exception is recorded as a failed row for the running case plus `travel-cross-system-fault.txt`); a checkpoint always says `INCOMPLETE`, and a timeout records the last native position, warp/jump state and the wait it was in. `Assert-TravelCrossSystemReceipt` re-checks the outputs exactly like the in-system phase (launcher outcome, declared phase and budget, required-case list, per-case status agreement, counts, session identity and resolvable evidence references), so a passing in-system receipt can never stand in for it.

**Fixture requirement.** `cross-system-jumpgate` needs a usable non-tutorial gate in the start system. `cross-system-wormhole` needs a usable, discovered wormhole with a native connection. If the fixture lacks that pair and the optional creation selection below is absent, the case records NOT-RUN with the observed counts. A missing required case fails the phase; it is never an empty PASS.

### Optional native wormhole fixture (`-TravelWormholeFixture`)

`-TravelWormholeFixture` is an additional Prepare selection that requires `-TravelCrossSystem` (and therefore `-TravelStation` and Full). It writes `travel-wormhole-fixture.enabled` and records `travelWormholeFixture` in provenance; marker and provenance must agree exactly, so an unselected run can never create content and a selected run is recorded as such. It creates **disposable sandbox test data only**, inside the freshly loaded fixture clone whose save directory is already redirected and guarded; the original saves are never written and no external network is involved.

It is attempted only when the loaded world has no usable pair, and only with the game's own factory, bound by its exact declared static signature and pinned by an installed-assembly test: `WormholeSpawner.PlaceWormhole(SystemMapData, bool, List<Wormhole>) : Wormhole`, which itself calls `SystemMapData.SetupPOI` and adds the POI to that system. The destination side is created first, then the current-system side with the destination as its declared target, then the reciprocal guid is completed on the list the factory itself assigns. No campaign-wide mutation is performed: `GamePlayer.UnlockWormholes`/`WormholeSpawner.SpawnWormholes` are deliberately NOT called, because directed `targetWormholeGuids` make the created pair usable without unlocking the galaxy network. The destination is an actual other native system, excluding the current system, direct usable-gate neighbours (the native planner enqueues the current system's gate edges before its wormhole edges), pocket systems, sectorless systems and systems that already own a wormhole. It must additionally sit in the SAME native map quadrant (a system's quadrant is its sector's `quadrant` field, the authoritative parent reference) and in a sector the player can already reach: the player's own sector, or one the game's own read-only `SectorMapData.IsUnlocked()` reports as unlocked. The remaining candidates are ordered deterministically by map distance then guid.

Only the SYSTEM choice is deterministic. The native factory positions the POI through `SystemMapData.GetRandomPosition()`, which consumes the clone's `SeededRandom.Global` stream, so the created wormhole's map position (and the seeded stream after it) differ between runs inside the disposable clone. No original save, profile or RNG state outside that clone is read or changed, and the phase never writes the clone back.

Creation happens BEFORE the case window opens and is followed by a bounded verification that the native planner really routes the created pair (`FixtureCreationSeconds`, part of the published budget). It is recorded as the OPTIONAL row `wormhole-fixture-setup`, never as a required case, with before/after wormhole counts, the created identities, the selection flag and the exact factory signature. Fixture preparation must be inert on the travel surface: a request, departure, arrival, completion or placement observed during the creation window fails the phase, and the launcher refuses a preparation row that was recorded without the selection, recorded twice, claims observed travel events, omits the factory signature or reports a nonzero fact count. After creation the route is driven and asserted exactly like the unmodified phase — approach, native gate/wormhole charge, native jump routine — with no manually started coroutine and no pointer teleport, and public readiness/location stay whatever the world reports until the real route runs.

This is native fixture data creation for a controlled probe. It is not API-authored managed content, content-persistence qualification or full in-game acceptance.

After both cases the phase reloads `fixture-a` so the later pilots see the same world state; that restoring load is harness cleanup and is never recorded as coverage. This remains controlled native evidence only: `RuntimeQualified=false`.

## Native travel resilience phase (separate, optional)

`-TravelResilience` is an ADDITIONAL Prepare selection that requires `-TravelStation` (it reuses the same native travel service), writes `travel-resilience.enabled`, and records its own reservation (`travelResilienceBudgetSeconds`) in provenance. It is independent of `-TravelCrossSystem`. It runs as its own phase `travel-resilience-v1` with its own receipts (`travel-resilience.txt`, `travel-resilience-receipt.tsv`, `travel-resilience-events.tsv`, `travel-resilience-fault.txt`) and its own mandatory case identities: `empty-origin-reroute`, `restore-relink-dock` and `stale-session-replay` — the three residual travel-matrix cells — plus the mandatory subcase row `restore-reinit-same-size`, which the receipt declares (`required-subcases=`) and the launcher checks separately from the case identities. It never widens the other two phases, whose optional rows for exactly these cells stay NOT-RUN.

Budgets add up rather than replace: `Run` refuses to launch unless `-TimeoutSeconds` covers base 1800 + every selected phase reservation (travel/station 1500, cross-system 2400, resilience 2400; **8100** with all three). As in the other phases the published `budgetSeconds` is SUMMED from the same per-wait deadline constants the driver uses, the pilot refuses to run when the shared harness wait/settle deadlines or the launcher reservation no longer cover it, and a receipt whose budget exceeds the reservation is refused. The wait plan is DERIVED from the number of required cases and from the per-case call-site multiplicities (two fixture-load/binding waits per case plus the stale case's own replacement load and the restoring load, three loaded-dock settles, two travel-availability samples, four undocks, one departure, one arrival, one route boundary, three restore assignments, one arrival dock, two bounded replays and the settles of every case), so a hand-typed occurrence cannot understate it. The published worst case is 2166 seconds against the 2400 reservation.

Every case loads `fixture-a` itself and captures its native owner — the exact `TravelManager` instance and player — only at its own load/readiness boundary, never earlier and never re-bound, with the same identity and liveness requirements as the cross-system phase. Every native drive and every observation snapshot first proves that the captured owner is still the live current one in the same session. Read-only snapshots record only current-POI knownness, live local manager, `TravelActive()`, `isWarping`, the player docking state and the location key; ship positions are deliberately never read, and no native docking state, location or waypoint is ever written by this phase.

- `empty-origin-reroute`: a real in-system route is started from the loaded start station and allowed to reach the verified origin unload (the public `Departed`); the pilot then PROVES the origin is really gone (native `currentPointOfInterest == null` and no live local manager, never a forced field) before `CancelTravel(null)` and a new `TryInitiateTravel` to a different POI. The asserted stream is exactly `Requested`->`Departed`->`Cancelled` for the abandoned leg and `Requested`->`Departed`->`Arrived`->`RouteCompleted` for the re-routed leg under a distinct operation identity, with the cancellation reported at an UNKNOWN location (never back at the origin), the re-routed departure reported with an unknown origin and sampled while the native warp loop is actually running (`isWarping`), and no fabricated departure in the frame a request was accepted.
- `restore-relink-dock`: the native assignment paths that are NOT docking requests must stay silent while the ship really reaches physical `Docked`. It covers the loaded fixture's own restore (`InitializePoi(init: true)` -> `AssignClosestDockingOption(ship, init: true)`), the relink entry point (`SpaceShip.InitSpacestationAutoActions` -> `RelinkDockedShipToStation`, `skipCoroutine: true`) and BOTH docking-size branches of the ship re-init. The SAME-size branch is MANDATORY and needs no second owned ship and no inventory transfer: it is driven as the native re-init of the CURRENT owned ship (`GameplayManager.ReinitPlayerSpaceship` alone), which is exactly what vanilla's own hangar equipment/module actions call when the changed ship data IS `GamePlayer.currentSpaceShip`; the inspected `ReinitPlayerSpaceshipRoutine` compares the current data against the live unit only in a DISCARDED expression, so it really re-spawns the unit and takes the `skipCoroutine` assignment on the unchanged option. It is asserted by the replaced ship UNIT, the preserved ship DATA, the unchanged docking-option identity/size and the unchanged `currentDockingOption`, and recorded as the mandatory subcase row `restore-reinit-same-size` — a missing, duplicated, not-run or failed subcase row is refused by the launcher, so there is no optional NOT-RUN for it. The DIFFERENT-docking-size branch — the one that takes a real `Dock()` coroutine outside any request, driven as the hangar swap (`GamePlayer.SetSpaceShipData` + `ReinitPlayerSpaceship`) minus its inventory transfer — is also REQUIRED and is proven from the native docking option the routine actually used (identity change plus the option size and the exterior manager's own `currentDockingOption`), so a same-size-only run can never be reported as two-size coverage. If no owned ship with a different docking size is eligible, the required case records NOT-RUN with the real per-size owned-ship counts, which is a phase FAILURE by design (an explicit fixture can be selected later). A positive control in the same session and window — the player's own exit action followed by the HUD dock button (`HudManager.Dock` -> `CheckForDocking`) — must still produce exactly one `DockedPhysical`, so a permanently silent observer cannot pass the case.
- `stale-session-replay`: two real `DockingOption.Undock()` coroutines are CAPTURED under session A (creating an iterator runs no native code, but the API's own factory hook wraps them and pins their session/player ownership). One is never advanced there; the other is advanced exactly one step so it produces its genuine session-A `Undocking` fact. A replacement fixture load then destroys that world — the pilot refuses to continue unless the captured option and ship really are destroyed — and both are advanced afterwards through a bounded Unity-frame replay with Unity's own child-iterator nesting, a step budget and a deadline. The replay must produce no travel fact, no physical station fact and nothing attributed to the replaced session; the capability, both public services and the replacement session's location must be unchanged; and the replacement session's own native undock must still emit `Undocking`->`Leaving`. A vanilla exception thrown by vanilla on its own destroyed objects is recorded with its actual type and throwing member (the API must not suppress it) and is never on its own treated as a pass. The replay method was selected by source audit: the inspected `Undock()` acts on the old option's own fields and vanilla's own liveness check skips the tail that would touch the live world.

Receipts are checkpointed atomically after every case and written on every path (an exception is recorded as a failed row for the running case plus `travel-resilience-fault.txt`); a checkpoint always says `INCOMPLETE`, and a timeout records the last native travel/dock position and the wait it was in. `Assert-TravelResilienceReceipt` re-checks the outputs exactly like the other phases (launcher outcome, declared phase and budget, required-case list, per-case status agreement, counts, session identity and resolvable evidence references), so a passing in-system or cross-system receipt can never stand in for it. After the last case the phase reloads `fixture-a` so the later pilots see the same world state; that restoring load is harness cleanup and is never recorded as coverage.

**Fixture requirement.** All three cases need `fixture-a` to load docked at a player-friendly station; the re-route case additionally needs two targets from the shared safe in-system selector (industrial POIs only, see the in-system phase above) and the restore case needs an eligible owned ship of a different native docking size. No content-creating ship fixture selection is offered; a world without an eligible ship records NOT-RUN and fails the required case. A passing phase remains bounded controlled native evidence: `RuntimeQualified=false`.

## Native travel recovery/continuation phase (separate, optional)

`-TravelRecoveryContinuation` is an ADDITIONAL Prepare selection that requires `-TravelStation`
(it reuses the same native travel service) and Full, writes `travel-recovery.enabled`,
and records its own reservation (`travelRecoveryBudgetSeconds`) in provenance. It is independent of
`-TravelCrossSystem` and of `-TravelResilience`: it drives its own routes, including its own gate
route. It runs as its own phase `travel-recovery-continuation-v1` with its own receipts
(`travel-recovery.txt`, `travel-recovery-receipt.tsv`, `travel-recovery-events.tsv`,
`travel-recovery-fault.txt`) and its two mandatory case identities `recovered-placement` and
`post-gate-continuation` — cells outside the other phases' scope. It
never widens them, and their optional rows for these cells stay NOT-RUN.

Budgets add up rather than replace: `Run` refuses to launch unless `-TimeoutSeconds` covers base
1800 + every selected phase reservation (travel/station 1500, recovery/continuation **4200**;
**7500** for the minimal selection this phase needs, 9900 with the cross-system phase). The
published `budgetSeconds` is SUMMED from the same per-wait deadline constants the driver uses (two
fixture-load/binding waits per case plus the restoring load, one undock per case, one availability
sample per recovery attempt and one for the continuation route, one departure per recovery attempt
plus the continuation's three legs, the arrival-or-cancel-window waits, one readiness placement, the
gate handoff, the jump arrival, the route boundaries and every settle), so a hand-typed occurrence
cannot understate it; the published worst case is 4112 seconds against the 4200 reservation (the
3932 s of the driven waits plus the bounded cleanup-settlement wait of 60 s, declared at most once
per attempt). Both
cases load `fixture-a` themselves and capture their native owner only at their own load/readiness
boundary, and every native drive and observation first proves that owner is still the live current
one in the same session.

- `recovered-placement`: the POSITIVE native `RecoveredPlacement` case. A real in-system route to a
  safe target is driven to its verified origin unload (the public `Departed`), and the pilot then
  samples the loaded world every frame until the native travel routine has assigned the destination
  POI to the player, its manager reports `initializedAndReady` and the native route is STILL RUNNING
  (`TravelActive()`), while `SpaceshipHasArrived` has NOT run (no public `Arrived`). In exactly that
  window it takes the player's own cancel action (`TravelManager.CancelTravel(null)`). The
  live-route requirement is load-bearing and is asserted by the pure rule from an ACQUISITION
  snapshot taken in the frame the cancel is about to be issued: on the inspected build the routine's
  own wait predicate (`<Travel>b__84_0`) returns TRUE when no local manager is registered, so a
  route can end silently without an arrival and the destination manager can initialize afterwards -
  readiness alone would then look identical to this window while nothing was travelling, and a
  cancel there would publish a `Cancelled` that interrupted nothing. Such an acquisition is
  classified as a MISS and no cancel is issued. An installed-assembly test pins that predicate
  shape. The API then has a placed session, no pending leg and an
  unknown location while the world reports a loaded, ready POI, and the adapter's own per-frame
  readiness observation (`Tick` -> `ObservePlacement`) publishes the placement. The asserted stream
  is exactly `Requested`->`Departed`->`Cancelled`->`RecoveredPlacement`, with the cancellation at an
  UNKNOWN location, and the placement carrying no operation identity, no origin, no requested
  destination, no dwell and mode `Unknown`; the native snapshots must show the departure with the
  origin unloaded and both the cancel and the placement at the destination POI with its manager
  initialized, no native route running and no waypoint left. Nothing is injected: no adapter
  callback is invoked, no location, waypoint or docking state is written, and no production hook is
  disabled. Whether the window is observable at all is NOT asserted: the installed-assembly test
  pins the native predicate SHAPE only, not Unity's coroutine or `CustomYieldInstruction`
  scheduling, and the window depends on when the destination manager finishes its own `Init`
  coroutine relative to the POI assignment. The bounded three attempts exist for exactly that
  fixture/readiness/scheduling variability, and every miss is recorded with its own reason; no
  "never misses" claim is made anywhere.

  Each attempt is persisted as its own NOT-RUN receipt row the moment it starts and rewritten with
  its TERMINAL outcome, so an attempt that later throws can never erase the earlier attempts'
  results. The outcomes are a committed whitelist (`cancelled-in-live-window`,
  `native-arrival-first`, `route-already-ended`, `timeout-no-route`, `timeout-route-running`,
  `native-travel-refused`, `no-safe-target`, `abandoned-leg-not-closed`,
  `cleanup-placement-unsettled`); a row still in its
  `state=started` form names no outcome and is refused, so it can only be a failure artifact.

  A MISSED attempt closes AND settles its own leg before the next one starts. The leg departed and
  never arrived, so it is still pending; left open, the next route request would supersede it and the
  tracker would truthfully publish that leg's `Cancelled` INSIDE the next attempt's window, failing
  an otherwise good attempt on the exact four-fact rule. The miss therefore issues the player's own
  `CancelTravel(null)` inside its own window and PROVES the observed closure is its own operation's
  (`Requested`->`Departed`->`Cancelled`).

  Closing that leg also OPENS the reducer's recovery gate (no pending leg, no current place), so the
  adapter publishes one `RecoveredPlacement` as soon as the native manager reports readiness —
  exactly the readiness the miss did not observe. Left unsettled it would land in a later attempt's
  quiet window and fail that attempt through the harness's own doing, so the miss waits for it,
  bounded (60 s, declared in the plan), and requires the placement to be observed at a manager the
  case still owns, reporting readiness, with no native route running again. That placement is never
  counted as coverage: the attempt never acquired a live window, so its acquisition can never satisfy
  the positive rule. Nothing is reset in the API or in native state and no late event is ignored. If
  the closure cannot be proven, or the placement does not settle within the bound, the case ends
  immediately with a NOT-RUN (`abandoned-leg-not-closed` / `cleanup-placement-unsettled`) instead of
  retrying on a window the harness itself could contaminate.

  Each miss persists its KNOWN reason BEFORE that cleanup's side effect, marked `cleanup=pending`,
  and rewrites the same row with the cleanup result afterwards, so a throw inside the cleanup cannot
  lose the reason. A receipt still carrying the pending marker describes an attempt that never
  finished and is refused by both validators; `state=started` stays reserved for a genuinely unknown
  failure.

  If no attempt observes the window the case records a mandatory NOT-RUN — a phase FAILURE by
  design — and never a pass. A window wait that expires while the native route is STILL RUNNING is
  not a clean miss and is never retried: the outcome and the residual observed AT the fault are
  persisted before any cleanup, the ordinary player cancel is then issued within the captured owner,
  and the post-cleanup residual is added beside it — the receipt publishes both
  (`preCleanupResidual=` and `postCleanupResidual=`), neither overwriting the other — and the case
  fails at the timeout. That cleanup is honest, not a claim of a quiet world: the vanilla
  cancel does not reset `isWarping` (only the end of `TravelInSystem` does), so a mid-warp timeout
  leaves that flag stale and the receipt says so. Nothing is written to hide it — no position, no
  warp state, no native field — and a failed phase leaves no world a later phase may continue from;
  the harness fails and the runner quits.
  This phase does NOT reach the native fast lane (gate-to-gate, `travelMultiplier = 7`), which needs
  a route whose next waypoint is another usable gate. The separate fast-lane phase covers that branch.
- `post-gate-continuation`: ONE native multi-waypoint route is requested to a safe follow-on POI in
  the system behind a usable non-tutorial gate, exactly as the map travel action does it. The native
  planner (`GenerateShortestRoute`) must really produce `[gate, follow-on]`; the in-system approach
  leg reaches the gate, the gate's own arrival hands the ship to the jump routine
  (`JumpGateManager.SpaceshipHasArrived` -> `InitiateTravelThroughGate`), and the jump routine ends
  with `TravelToNextWaypoint`, which starts the post-gate in-system leg. The asserted stream is three
  legs with distinct operation identities and exactly ONE `RouteCompleted`, at the end, belonging to
  the post-gate leg — the cross-system phase's own route rules are reused for the per-leg identity,
  mode and origin/requested/actual checks. The decisive native evidence is the snapshot at the gate
  arrival: a waypoint still remained and the jump routine still owned the transition, so withholding
  the completion there is observed truth rather than a timing artefact. The pilot additionally
  refuses any completion observed before the native route really ended (waypoints empty, no
  `TravelActive()`, no `usingJumpgate`). The follow-on POI is chosen with the SAME shared refusal
  rule the in-system phases use, applied to the destination system, so the chain cannot end in a
  station, another gate, a dynamic event or a native combat encounter. The one-way tutorial exit gate
  (`Hermetis` -> `Canis Majoris`) is excluded by identity and stays source-attested only.

Receipts are checkpointed atomically after every case and written on every path (an exception is
recorded as a failed row for the running case plus `travel-recovery-fault.txt`); a checkpoint always
says `INCOMPLETE`. `Assert-TravelRecoveryReceipt` re-checks the outputs exactly like the other phases
and additionally requires each case to PUBLISH the native evidence it claims: the recovery row must
carry a recovered location, an ACQUISITION snapshot with `travelActive=True` and `managerReady=True`
(the live route the cancel interrupted) and a placement snapshot with `managerReady=True`,
`travelActive=False` and `waypoints=0`, and the continuation row must carry `legs=3`, `routeCompletions=1`, a gate-arrival
snapshot with `usingJumpgate=True` and a remaining waypoint, and a completion snapshot with no
waypoints and no active native travel. The attempt log is validated too: at least one persisted
attempt row, never more than the declared bound the receipt itself publishes (`recovery-attempts=`),
every row NOT-RUN with a named outcome, and exactly one row reporting the live-route cancel whenever
the case passed, no attempt still marking its own cleanup pending, a settled cleanup recovery
published by every miss that closed its own leg, plus contiguous unique numbering from 1 within the
COMMITTED bound (never a bound
the receipt declares for itself), the case's own session on every attempt row, whitelisted outcomes
only, and the single success as the LAST attempt with every earlier attempt a continuable miss.
After the last case the phase reloads `fixture-a` so the later
pilots see the same world state; that restoring load is harness cleanup and is never coverage.

**Fixture requirement.** Both cases need `fixture-a` to load at a known native system/POI (docked is
fine: the phase uses the player's own exit action first). The recovery case needs at least one, and
preferably three, safe in-system targets; the continuation case needs a usable non-tutorial gate whose
destination system contains a safe follow-on POI the planner routes to as `[gate, follow-on]`. A world
that offers neither records the honest NOT-RUN above, which fails the phase rather than shrinking it.
This remains controlled native evidence only: `RuntimeQualified=false`.

## Native fast-lane phase (separate, optional)

`-TravelFastLane` is an ADDITIONAL Prepare selection that requires `-TravelStation` (it reuses the
same native travel service) and Full, writes `travel-fast-lane.enabled`, and records
its own reservation (`travelFastLaneBudgetSeconds`) in provenance. It is independent of
`-TravelCrossSystem`, `-TravelResilience` and `-TravelRecoveryContinuation`. It runs as its own
phase `travel-fast-lane-v1` with its own receipts (`travel-fast-lane.txt`,
`travel-fast-lane-receipt.tsv`, `travel-fast-lane-events.tsv`, `travel-fast-lane-fault.txt`) and its
two mandatory case identities `fast-lane-gate-chain` and `fast-lane-multiplier-observed`.

**Why it needs its own route.** The native fast lane is a different branch from the post-gate
continuation: `GamePlayer.DoFastLaneTravel()` is true only when `waypoints[0]` is a usable
`JumpGate` and the save's own `fastLaneTravelUnlocked` is set, and only then does `JumpToSystem`
charge the next gate (`TheGate.ChargeFastLaneTravelToNextGate`) and store `travelMultiplier = 7f`.
The continuation phase deliberately ends at a safe non-gate POI, so it can never take that branch.
This phase therefore drives ONE native planner route across TWO gates into a THIRD system: the
planner (`GenerateShortestRoute`) must really produce `[gate, gate, destination]`, so in the
intermediate system the next waypoint is another gate.

- `fast-lane-gate-chain`: the route shape. Five legs — in-system approach, gate hop, the
  gate-to-gate in-system leg, the second gate hop, the final in-system leg — with distinct operation
  identities, the cross-system phase's own per-leg identity/mode/origin/requested/actual rules, and
  exactly ONE `RouteCompleted`, as the last fact, belonging to the final in-system leg and to
  neither gate hop. The pilot additionally refuses an early completion at each of the four
  intermediate stages, and compares the ending world (destination POI current and initialized,
  waypoints empty, no `TravelActive()`, no `usingJumpgate`) against the public location.
- `fast-lane-multiplier-observed`: the actual proof that the branch RAN. Every fact of the
  gate-to-gate leg must be sampled with the native `travelMultiplier` at **7** and
  `fastLaneTravelActive` true, outside the jump routine and with a waypoint still queued, while the
  approach leg before it and the final leg after it (and the route boundary) are sampled at 1. On
  the inspected build `travelMultiplier = 7f` is stored in exactly ONE place in the whole assembly —
  inside the jump routine's state machine, immediately after the charge coroutine — and an
  installed-assembly test pins that uniqueness, so observing the 7 is proof of the branch rather
  than proof of the unlock flag or of a route that merely contained gates. The two snapshots
  (`fastLaneTravelActive` and the multiplier) are read from the same live manager and must agree.

**The unlock flag is only READ.** The phase never writes `fastLaneTravelUnlocked`, any other native
field, any save or any config: a fixture whose own history did not unlock the fast lane records an
honest NOT-RUN, which is a phase FAILURE by design, and the receipt publishes
`fastLaneUnlocked=True (read-only; never written)` as the precondition it observed. Gate selection
uses the phase's own pure refusal rule (usable, not hidden, not dynamic, leaves its own system, has
a paired target, not hostile-owned, not a story-mission location, no persisted guard descriptors,
never the one-way tutorial exit), and the destination uses the shared safe in-system target rule, so
the chain cannot traverse or end in a hostile, guarded, dynamic or tutorial location.

Budgets add up rather than replace: `Run` refuses to launch unless `-TimeoutSeconds` covers base
1800 + every selected phase reservation (travel/station 1500, fast lane **2400**; **5700** for the
minimal selection this phase needs). The published `budgetSeconds` is SUMMED from the same per-wait
deadline constants the driver uses (one fixture load with its service binding plus the restoring
load, one undock, one availability sample, three in-system arrivals, one handoff and one jump
arrival per gate, one route boundary and three settles), so a hand-typed occurrence cannot
understate it; the published worst case is **1960** seconds against the 2400 reservation. The phase
drives exactly ONE route and never retries: a timeout or a refused native call is a recorded
failure, never another attempt.

`Assert-TravelFastLaneReceipt` re-checks the outputs exactly like the other phases and additionally
requires each case to PUBLISH the evidence it claims: the chain row must carry `systems=3`,
`gates=2`, `legs=5`, `routeCompletions=1` and a completion snapshot with no waypoints and no active
native travel; the multiplier row must carry `fastLaneMultiplier=7` with `fastLaneActive=True`, the
surrounding legs at 1, the read-only unlock precondition, and each of the gate-to-gate leg's three
boundary snapshots at the charge multiplier. Any row whose case identity is not one of the two the
phase owns is refused outright, in both the C# rule and the launcher validator. After the case the
phase reloads `fixture-a` so the later pilots see the same world state; that restoring load is
harness cleanup and is never coverage.

**Fixture requirement.** `fixture-a` must load at a known native system/POI (docked is fine: the
phase uses the player's own exit action first), its save must already have `fastLaneTravelUnlocked`
set by its own history, and the world must offer two safe non-tutorial gates leading into a third
system with a safe follow-on POI the planner routes to as `[gate, gate, destination]`. A world that
does not meet these requirements records NOT-RUN, which fails the phase rather than shrinking it.
This remains controlled native evidence only: `RuntimeQualified=false`.

## Unqualified tutorial exit rewrite

The supported tutorial exit path has source and host-test evidence, but no controlled native qualification. On the inspected assembly, `GamePlayer.TransitionTutorialToSandbox` has one call site, `TravelManager.<JumpToSystem>d__103.MoveNext`; it rewrites the gate target and player's world in one direction only. The runner has no tutorial-exit phase.

The contract retains the raw `targetSystemGuid`/`targetPoiGuid` in `Requested` and reports the actual rewritten destination in `Arrived`, under the same operation identity. Do not infer native coverage from ordinary gate travel or introduce an undocumented command-line selection. Qualifying this path requires separately authorized tooling and a disposable tutorial-stage fixture.

## Actual-consumer travel probe (separate, optional)

`-AnimaTravelProbe` is an ADDITIONAL Prepare selection that requires the authorized Anima consumer pilot (`-AnimaBin` / `-AnimaRevision` and therefore `-MissionTransitionsProbe -MissionIdentityProbe -PersistenceProbe -MissionJournalBin`), `-TravelStation`, `-TravelCrossSystem` and `-TravelWormholeFixture`. It writes the marker `anima-travel.enabled` and records `animaTravelProbe`, `animaVersion` and its own reservation (`animaTravelBudgetSeconds`) in provenance. Only the Anima **0.4.0 / hard API 0.1.9** metadata shape is accepted for it (the 0.3.0 / 0.1.8 shape is accepted only for the mission pilot), and the marker, the provenance flag, the pinned consumer version and the wormhole-fixture selection must all agree.

It runs as its own phase `anima-travel-consumer-v1` with its own receipts (`anima-travel.txt`, `anima-travel-receipt.tsv`, `anima-travel-events.tsv`, `anima-travel-fault.txt`), its own mandatory case identities `consumer-binding`, `non-travel-quiet`, `gate-arrival-visit`, `wormhole-arrival-visit`, `visit-persistence` and `recording-degraded`, and the mandatory subcase rows `gate-visit-reload`, `gate-visit-rollback` and `regional-history-fixture`, which the receipt declares (`required-subcases=`) and the launcher checks separately from the case identities.

**It drives no travel of its own.** The arrivals it reasons about are driven and asserted by the two native travel phases: it REUSES `travel-in-system-station-v1` and `travel-cross-system-v1` in place instead of repeating or re-implementing them, and it never turns their rows into its own coverage. What it adds is the ACTUAL installed consumer's side of those same public facts: what Anima's own visited-system registry, its own v4 sidecar, its own session/leg latch and its own degraded state did with the arrivals the API published.

**Ordering contract.** The existing Anima mission pilot deliberately ends with `StopProvider`, which permanently disposes the consumer's visit observer, so the consumer probe MUST observe the travel phases before it. When the probe is selected it owns the ordering: it wraps both travel phases and runs them right after the mission-identity pilot and before `CheckAnimaMissions`. Each phase keeps a run-once latch, so the later call sites become no-ops and neither phase can be run — or receipted — twice. When the probe is not selected, nothing moves. `Assert-AnimaTravelReceipt` re-checks that ordering from the run's own `result.txt`: both reused phase scenarios and the probe must each be recorded exactly once, both reused phases before the probe, and the probe before `native-anima-api-missions`.

Budgets add up rather than replace: `Run` refuses to launch unless `-TimeoutSeconds` covers base 1800 + travel/station 1500 + cross-system 2400 + this probe's own **1200** (**6900** for that selection; 9300 when the resilience phase is also selected). The probe reserves only its OWN waits, because the reused phases keep their existing reservations. The multiplicities are DERIVED from a per-method call-site plan (`AnimaTravelReceipt.CallSites`), where each entry declares how often the phase invokes that method and how many loads, placement waits, quiescence samples and settles it contains: 1 binding hook, 1 in-system completion, 2 cross-system ready hooks, 2 cross-system completion hooks (2 loads, 2 placements, 3 quiescences, 2 settles each), the degradation case and the historical-input fixture. That derives **5** probe slot loads, **9** placement waits, **13** quiescence samples and **6** settles, and the published worst case is **992** seconds against the 1200 reservation. A host test re-counts the pilot's ACTUAL source call sites against that plan, so undeclared waits fail the budget check; a future case that adds loads or waits must raise the plan, the reservation and the Run minimum with it.

What each required case asserts, always from a baseline taken at the freshly loaded session BEFORE the case and a delta taken after callback quiescence (the consumer subscribed at its own Awake, so it is dispatched first; the probe still waits for an idle public dispatch and several fact-free frames so it can never race the consumer):

- `consumer-binding`: the installed consumer is active and recording, its observer is the same instance its load hook owns, the public travel service is bound to the phase's session, the `native-travel` capability is available, the restored registry equals the loaded slot's own sidecar, and the consumer owns NO live Harmony patch on the native travel/station surface (`TravelManager`, the travel/docking managers, `SetSystemEntry`, the jump routines, `Dock`/`Undock`, ...). The pure refusal rule is host-tested, and an installed-consumer Cecil test additionally proves the shipped consumer IL contains no `Behaviour.Managers.TravelManager` reference at all.
- `non-travel-quiet`: the whole in-system phase — initial placement, undock, request, early cancel, in-system POI arrivals, chained route, final route completion and the arrival dock — leaves the visited-system history byte-identical. The window must really carry a placement, a request, a cancellation, an in-system arrival and a route completion (and no cross-system arrival), so the negative claim can never be vacuously satisfied by an empty window.
- `gate-arrival-visit` / `wormhole-arrival-visit`: the single witnessed `Arrived` of that case's mode increments the count of the ACTUAL arrival system by exactly one — never the requested nominal system of a redirected hop, never twice, and never any other system — at the public event's own `GameSeconds`, preserving the stored label when the public location carried none. The probe then writes a real vanilla save through the harness's own save helper (the consumer's own save hook writes the sidecar; the probe never writes a consumer file), reads that sidecar back through the consumer's own reader and path resolver, and requires the persisted rows to be the current v4 schema, to equal the live registry AND to satisfy the same public-event comparison. Baseline history is never truncated.
- `regional-history-fixture` (mandatory subcase of `recording-degraded`, see below).
- `gate-visit-reload` / `gate-visit-rollback` (mandatory subcases): reloading the saved slot restores exactly the saved counts and resets the consumer's per-session leg latch (replaced session, zero counted legs); loading the earlier fixture rolls the history back to that slot's own counts instead of silently keeping the newer session's registry. These run at the gate boundary BEFORE the wormhole case's own fresh fixture load replaces the registry, so the proof can never be deferred past the point where it would be lost. `visit-persistence` is the same pair of proofs for the wormhole slot.
- `recording-degraded`: a controlled visit-only consumer fault — the consumer's OWN documented observer-failure path, not a fault of the API travel hub, which the later phases still need — stops visit recording. The case requires a POSITIVE regional-recognition window BEFORE the fault and its omission after: an already-omitted window would prove nothing. Both samples come from the consumer's own shared production decision, `RegionalRecognition.ForCurrentContext()`, the exact method its bar context path calls; it reads the live recording gate, registry, journal bridge and clock ITSELF, so the probe cannot supply a substitute flag or history and does not re-implement the gate anywhere. Its result is handed to the consumer's own `ContextGatherer.Gather` and serialized with the consumer's own serializer, so the presence of the `regionally_known` key is the consumer's own output. The case then also requires: the observer latched and released, the consumer's recording flag false while the `native-travel` capability and the public service stay available (a LOCAL latch, not an API failure), the mission provider still active with its save writes enabled and both load-safety hooks intact, no travel fallback hook installed, and the recorded history preserved and still written to a new sidecar.

  **Authorized disposable historical input (`regional-history-fixture`).** Recognition needs three visits to one system, which no arrival this phase can legitimately drive produces, so after EVERY genuine arrival, save, reload and rollback proof the probe saves its OWN new sandbox slot, reads that slot's freshly written consumer sidecar through the consumer's own reader, raises the current system to the consumer's own `MinVisitsThreshold` while preserving every other recorded system and every first-visit time, writes it back through the consumer's own schema type and writer, and loads that slot through the consumer's real load path. It refuses to touch anything but its own named slot inside the redirected sandbox save directory (path containment plus an explicit refusal to write beside `fixture-a`/`fixture-b`), and it refuses to rewrite a companion the consumer cannot read or that declares another schema version rather than overwriting it. The row records it as SYNTHETIC HISTORICAL INPUT with the before/written/restored system counts and `countedVisits=0`: the load window must contain no arrival, and the restored history must equal exactly what was written, so fixture input can never be reported as a witnessed visit. No original save and no prepared fixture companion is touched, and selecting `-AnimaTravelProbe` is the authorization for this phase-owned fixture; marker and provenance pin that selection.

Receipts are checkpointed atomically after every case and written on every path; a checkpoint always says `INCOMPLETE`. A consumer case that runs inside a reused travel phase records its own failure in its own receipt instead of faulting that phase, so a consumer defect is never attributed to a native travel case that actually passed — the consumer phase still fails, because a failed row can never satisfy a required case. Evidence references are explicit surface/API-sequence pairs and may legitimately name the SAME public sequences the reused phases referenced: they are facts, not exclusive case labels.

`TravelStationReceipt`/`AnimaTravelReceipt` rules (visit increment, label preservation, no-growth, history preservation, session replacement, sidecar path/version, patch refusal, evaluation) are compiled into the host test project, and `make check-consumer ANIMA_ASSEMBLY=<locally-built VGAnima.dll>` runs the installed-consumer metadata tests that pin every consumer member the probe reflects and prove the visited map can only grow inside the public-event observer. Those tests are excluded from `make test` and from `check-local` because they need a consumer binary, and they read it with Cecil against an explicit bounded dependency search path (see `check-consumer` and its dependency overrides in the [Makefile](https://github.com/fankserver/vanguard-galaxy-api/blob/main/Makefile)); they never load it into the test process.

A passing probe supplies controlled actual-consumer evidence for its own cases only. It is not automatic API-owned content persistence, Echo integration, the archived TravelJournal comparison or full in-game acceptance: `RuntimeQualified=false`.

## Actual-consumer Echo arrival-snap probe (separate, optional)

`-EchoTravelProbe` is an ADDITIONAL Prepare selection that requires the Echo consumer (`-EchoBin`
with a mandatory exact `-EchoRevision`), `-TravelStation`, `-TravelCrossSystem`, `-TravelWormholeFixture`
and Full. Only the Echo **0.7.0** metadata shape is accepted, and only with a **soft** BepInEx
dependency on the API: the same build has to keep loading when the API is absent, which the separate
control below exercises. Prepare writes `echo.enabled` / `echo-travel.enabled`, a sandbox-only
`vgecho.cfg` (`TimingEnabled=true`, `ArrivalSnap=true`, **`EtaSync=false`** so an ETA write can never
be mistaken for an arrival snap), and records `echo`, `echoRevision`, `echoVersion`,
`echoTravelProbe` and its reservation `echoTravelBudgetSeconds` in provenance. Marker, provenance
flag, pinned version, source revision and configuration must all agree.

**One consumer probe per run.** `-AnimaTravelProbe` and `-EchoTravelProbe` both OWN the two reused
native travel phases and their ordering, so selecting them together is refused at Prepare, refused
again in provenance validation, and covered by launcher tests. The Anima 0.4 mission pilot is
unaffected and can still be selected normally.

It runs as phase `echo-travel-consumer-v1` with its own receipts (`echo-travel.txt`,
`echo-travel-receipt.tsv`, `echo-travel-events.tsv`, `echo-travel-fault.txt`), the mandatory case
identities `arrival-snap-binding`, `no-snap-quiet`, `in-system-final-snap`, `gate-final-snap`,
`wormhole-final-snap`, `earlier-subscriber-supersession` and `snap-stop-degradation`, and the
mandatory subcase row `declared-probe-controls`. Budgets add up: `Run` refuses to launch unless
`-TimeoutSeconds` covers base 1800 + travel/station 1500 + cross-system 2400 + this probe's own
**1800** (**7500** for that selection; 9900 when the resilience phase is also selected). The
multiplicities are DERIVED from a per-method call-site plan (`EchoTravelReceipt.CallSites`) that a
host test re-counts against the pilot source; the published worst case is **1626** seconds.

**What is actually proven.** The probe calls nothing of the consumer's and nothing of the game's
idle cycle. Read-only Harmony prefixes/postfixes around the INSTALLED consumer's own
`AutopilotTimingPatches.ApplyArrivalSnap` and around native `IdleManager.Update` record the frame,
the native idle timer before/after and the live travel state; a prefix on `IdleManager.FindActivity`
counts the real native decision. A positive case therefore requires the consumer's own write to run
in the SAME frame as the public `RouteCompleted` fact, to move the timer from strictly positive to
exactly zero while the native world is quiet, and the NEXT native `IdleManager.Update` to start from
that zero and reach its `FindActivity` decision.

- `arrival-snap-binding`: the installed 0.7.0 consumer holds a live subscription for the freshly
  loaded session, its three configuration gates are present in the pinned shape, and no native route-boundary
  timing hook is installed. The rule is deliberately narrow: only a patch DECLARED BY
  `AutopilotTimingPatches` on the native route boundary is refused. Echo's unrelated automation (the
  opt-in refinery routing, default off) legitimately owns its own `TravelToNextWaypoint` postfix, so
  the probe never asserts that Echo owns no native travel hook at all; every owned patch is recorded.
- `no-snap-quiet`: with the native autopilot disengaged, the whole reused in-system phase — placement,
  requests, cancellation, intermediate arrivals AND final route completions — produces zero consumer
  writes and no snap-driven idle decision. The window must really carry those facts, so an empty
  window can never satisfy it.
- `in-system-final-snap`, `gate-final-snap`, `wormhole-final-snap`: the positive proof above, taken
  from the reused cross-system phase's own qualified completions (its in-system approach leg and its
  gate/wormhole hop).
- `earlier-subscriber-supersession`: the probe disposes the consumer's own observer, registers an
  EARLIER observer and then invokes the consumer's OWN production binder (`Plugin.BindArrivalSnap`),
  so the consumer's fresh subscription lands after it; the reordering is recorded and one live
  consumer observer is proven. It then drives a real owned in-system route, and from inside that
  route completion's dispatch the earlier observer issues a REAL native `TryInitiateTravel` to a
  safe industrial target from the shared safe-target selector. The consumer's write-time guard must
  then refuse: the timer is left exactly as it was, no snap-driven idle decision happens while the
  ship is busy, and the new route's own completion snaps under its own distinct operation identity.
- `snap-stop-degradation`: stopping the subscription disables ONLY the snap. A later real route
  completion writes no timer while the ETA-sync patch and the unrelated patches stay installed and
  no native timing hook returns; the production binding is then restored.

**Fixture preflight.** Every fixture this phase loads is checked BEFORE any unarmed setup wait: the
loaded save's own autopilot must be disengaged, and a case that has to engage it additionally needs
autopilot unlocked. The refusal names the slot and the session and points at the fixture, because the
suppression accounting is only meaningful while an idle decision outside an armed window is
impossible; an autopilot-on save would otherwise fail the phase minutes later with a diagnostic that
looks like a suppression defect. It fails closed: the probe never disengages a state it did not
create and never relaxes the no-autonomous-decision signal to accommodate one.

**Autopilot safety cleanup.** Engaging the consumer's gate captures the EXACT player and session it
was engaged on. A bounded cleanup then disengages only that player, only while it is still the live
current one of the still-current session; a replaced or destroyed owner, or a replaced session, is
left untouched and recorded as a declined release. It runs on the normal path, on the fault path of
both cross-system consumer hooks (after the failed row and its checkpoint are written, so a cleaned
world can never make a failed case look passed) and in the phase's outer `finally` as the last
resort for a fault raised elsewhere, including inside a synchronous API callback. It never throws
and is idempotent, and it adds no wait to the budget.

**Declared controls (`declared-probe-controls`).** The mandatory setup row names every bounded
control with its exact counts: the sandbox-only configuration, the autopilot releases (and any
declined ones with their reason), the native idle-timer seeding
(300 s, so a case never begins from an expired cycle and a natural expiry can never look like a
snap-driven decision), the bounded autopilot engagements, the read-only observation probes, the
counted `FindActivity` BODY suppression and the subscription reorderings. The suppression exists so
reaching the decision boundary cannot launch an uncontrolled autonomous route; it proves the
boundary was REACHED, never that the autonomous action executed, and the launcher refuses a controls
row that does not declare it. `TravelActive` guards are never suppressed and no travel is ever
manufactured.

### API-absent Echo load control (`-EchoAbsentProbe`)

A SEPARATE `MissingApi` sandbox selection (refused together with the Full probe) that requires
`-EchoBin`. The API-independent guard — which has no API assembly reference and gains no Echo
reference — checks through `Chainloader` and reflection that the same Echo 0.7.0 build loaded and
stayed enabled with no API plugin and no API assembly in the process, that `_arrivalSnap` is
unbound, and that its expected patches (including the autopilot timing hook on
`Behaviour.Gameplay.IdleManager.Update`) are installed. A guard-owned counter records real native
idle-update invocations, so plugin-load/patch evidence is reported separately from observed
execution: invocations only happen when the existing optional `-VanillaLoadControl` is selected too,
and the launcher refuses a receipt that claims invocations without it or reports none with it. This
covers unrelated Echo features loading without the API, not a claim that every Echo
automation feature behaves correctly without it.

Consumer metadata is additionally pinned by Cecil host tests
(`make check-consumer ECHO_ASSEMBLY=<locally-built VGEcho.dll>`), which read the candidate read-only
with the same bounded dependency search path as the Anima tests and prove the native route-boundary timing hook is
absent while the unrelated automation keeps its own, that the timer write is referenced only by the
binder that hands it to the API observer, and that no always-JIT plugin member names a VGModAPI type.

A passing probe supplies controlled actual-consumer evidence for its own cases only. It is not full
in-game acceptance, Echo's ETA-sync qualification, the archived TravelJournal comparison or
managed-content qualification: `RuntimeQualified=false`.

## Archived TravelJournal comparison (separate, optional)

`-TravelJournalComparison` is an ADDITIONAL Prepare selection that requires the archived plugin
(`-TravelJournalBin` with a mandatory `-TravelJournalRevision` and `-TravelJournalSha256`),
`-TravelStation`, `-TravelCrossSystem`, `-TravelWormholeFixture` and Full. It is refused together
with `-AnimaTravelProbe` or `-EchoTravelProbe`: all three own the same two reused travel phases, so
at most one owns a run. `-TravelJournalBin` without `-TravelJournalComparison` is refused as well,
and so are the pins without the binary: the archive patches the game, so it is never installed as a
passive ridealong. The comparison sandbox also carries the ARCHIVE ALONE: `-AnimaBin` or `-EchoBin`
beside it is refused by Prepare - before anything is copied - and again by provenance validation at
Run, so both boundaries enforce the same rule. That rule is about consumer plugins that observe the
same native travel and save surfaces; the unrelated Stockpile and MissionJournal selections are not
consumer plugins for this rule and neither side refuses them.

**The archive is never touched.** It is not edited, rebuilt, reactivated, migrated or bridged, and
nothing here calls its `IVgTravelJournal` surface, reflects into its store or invokes its patches.
Exactly ONE binary is accepted, and the pin is a COMMITTED CONSTANT in
`tools/qualification-inputs.ps1` (`$TravelJournalPinnedSha256`, `$TravelJournalPinnedRevision`), not
a caller argument: `-TravelJournalSha256`/`-TravelJournalRevision` must equal those constants AND
the bytes on disk, at Prepare and again during provenance validation, so a caller can repeat the pin
but can never widen it. Version metadata alone is never sufficient - a rebuild keeps the same
declared identity - but it is still required: assembly identity `VGTravelJournal 0.1.0.0` with
BepInEx plugin version `0.2.0` (deliberately different) and embedded `AssemblyInformationalVersion`
`0.1.0+<revision>`. Only the DLL is copied - never the PDB, never `deps.json` - and validation
refuses a deployed PDB.

`make check-archive TRAVELJOURNAL_ASSEMBLY=<dll> TRAVELJOURNAL_PDB=<pdb> [TRAVELJOURNAL_REPO=<dir>]`
re-attests the same binary read-only outside a run. It checks that the sibling PDB belongs to it
(CodeView id and stamp) and carries a SHA-256 hash for all 24 compiled documents, and it then
COMPARES those hashes against the archived revision's own committed sources: each of the 22
committed documents is read with `git show <pinned-revision>:<path>` (arguments passed as a list,
never through a shell) and must hash equal, exactly two generated files under the project's `obj/`
tree may have no committed source, and the compared set must equal every committed `.cs` file of the
project. The repository is `TRAVELJOURNAL_REPO` when given, otherwise the checkout the pinned binary
lives in; the working tree is never read, so uncommitted local drift can neither satisfy nor break
the comparison. The archive is only read - it is never built. That attests the compiled INPUTS of
that build, and no document path, private game code or binary content is published.

Sandbox configuration is `[Journal] Verbose = true`, `MaxEvents = 0`. Zero is the archived plugin's
own documented unbounded value, so its FIFO eviction can never silently drop a compared row. Both
settings are pinned by provenance validation through a parsed check of the file's effective
entries - not its bytes, because BepInEx rewrites the file at `Bind` - and a duplicated or
conflicting entry is refused rather than resolved silently.

**Isolation.** A read-only audit of the whole archived source found its entire persistence surface
to be the live static `SaveGame.SavesPath`, the `SaveGameFile` vanilla is loading, and the sidecar,
`.tmp` and quarantine siblings beside them - no `persistentDataPath`, no `PlayerPrefs`, no
environment or registry root, no network, no process launch. The existing guard redirect
(`SavesPath`/`SavesDir`/`_saves` plus the `Store`/`Recall` prefixes) therefore contains it with no
patching of the archive. The phase additionally snapshots the sandbox save directory itself: every
file the journal created must match its own documented patterns (`*.save.vgtraveljournal.json`,
`*.vgtraveljournal.corrupt.*.json`, `*.vgtraveljournal.json.tmp`) and lie under the audited roots,
which the receipt lists explicitly. That in-run case is bounded to its own phase: the archived
plugin still flushes when the game quits, so the LAUNCHER repeats the audit after the owned process
exited, over the explicitly named sandbox roots (`Saves`, `game`, `game\BepInEx`,
`game\BepInEx\plugins`, `game\BepInEx\config` and the sandbox root itself), records every
matching file as created, rewritten or unchanged against a pre-launch snapshot in
`travel-journal-postquit-audit.txt`, and refuses any journal file outside the sandbox saves or with
an unexpected name. The prepared plugin BINARY must hash exactly unchanged. Its configuration must
not: BepInEx 5.4 rewrites a plugin's config file on the first `Bind` (`SaveOnConfigSet`, which the
archive never disables), adding its own header, `##` descriptions and spacing. The audit therefore
re-validates the configuration by its PARSED semantics - `[Journal] Verbose = true`, `MaxEvents = 0`,
with comments and unknown keys benign but a missing, duplicated or changed effective value refused -
records the old and new hashes plus `semantics=pass`, and claims no byte equality for it. Prepare-time
and Run-time validation share that one parsed validator. No claim is made about locations that were not scanned.

It runs as phase `travel-journal-comparison-v1` with its own receipts and nine mandatory cases:
`legacy-binding`, `in-system-arrival-compatible`, `chained-arrival-compatible`,
`jumpgate-prefix-lead`, `wormhole-transit-gap`, `station-interior-vs-physical`,
`legacy-blind-concepts`, `journal-io-containment` and `api-dwell-anchored`. `Run` refuses to launch
unless `-TimeoutSeconds` covers base 1800 + travel/station 1500 + cross-system 2400 + this phase's
own **900**; its derived worst case is 350 seconds (3 placement waits, 10 quiescence samples and the
one bounded in-flight departure wait), because it drives no route and performs no load of its own.
Its eight real saves are declared in the same call-site plan, counted as actual invocations rather
than lexical call sites (in-system ready and completion, two cancel boundaries, one baseline per
cross-system case, the in-flight save and the wormhole completion), and carry no budget term: a save
is synchronous and adds no wait.

How the comparison works: the phase observes the PUBLIC travel and station surfaces, lets the
qualified phases drive, and takes a real vanilla save through the harness's own helper at each
compared boundary. The archived plugin's own postfix writes its sidecar; the phase then reads that
FILE with its own strict reader, bounded to 8 MB and 20000 events - a document beyond either bound
is REFUSED, never partially read - and compares by append offset relative to the baseline captured
for that window, so no global counter monotonicity is assumed across the archive's load-time reset.

Each window's baseline is a REAL save taken after placement and quiescence, never an assumed zero: a
fresh load makes the archive append its own station-dock and POI-arrival rows before any case drives
anything, and those rows belong to the baseline, not to the window. The baseline keeps the content
fingerprint of every row it captured, so a store that was reset and refilled - even with the same
number of rows - is refused instead of reading as an empty append window. Capturing a baseline is a
pure operation that returns one: only the two Ready hooks own the window's baseline, so a boundary
sample taken INSIDE a window (the cancel case) keeps its own and the enclosing window still
subtracts exactly what it captured.

- **Compatible pairs** (`in-system-arrival-compatible`, `chained-arrival-compatible`): the public
  in-system arrivals and the journal's own `PoiArrival` rows must agree on native POI guid, count
  and order. Identity only - names are NEVER compared, because the archived plugin reads the game's
  lazy name getter (`MapElement.name` -> `GenerateDefaultName`) and is therefore **not a passive
  observer**: installing it can generate names and perturb seeded world randomness relative to a run
  without it. The receipt acknowledges that explicitly, and no world-RNG equality is claimed.
- **`jumpgate-prefix-lead`** (driven): the phase waits - bounded, and declared in the call-site plan
  - for the leg's own public `Departed` while no `Arrived` exists and the native jump iterator is
  still running, then takes a real save. Everything the rule uses is captured AT that save on the
  main thread and never recomputed afterwards: the leg's `Requested`, `Departed` and `Arrived`
  booleans, whether the jump was still running, and `Time.frameCount`. "Lead" means OBSERVED EVENT
  ORDERING, not clock advance: the legacy row was already in a file written in a frame strictly
  before the frame in which the public arrival callback was delivered. The native clock can stand
  still across a jump, so an equal legacy and arrival game time is accepted while a legacy time
  after the arrival is not. If that in-flight interval never occurs the case fails honestly; no
  native callback is invoked and the archive's store is never bridged.
- **`wormhole-transit-gap`** (driven): the qualified wormhole hop produces a public arrival while the
  journal, which hooks `JumpToSystem(JumpGate)` only, records no transit for it.
- **`station-interior-vs-physical`** (driven): the journal's dock row is the interior scene toggle,
  so it either precedes the public `DockedPhysical` game time or does not exist at all; both
  outcomes are recorded from the observed evidence and a legacy row AFTER the physical dock fails.
- **`legacy-blind-concepts`**: the archive has no session, request or cancellation concept. The
  qualified in-system cancel case calls an inert boundary hook immediately before and immediately
  after itself; each boundary takes a real save and re-reads the journal file, so the window is the
  cancel's own and not an inference over the whole phase. The exact appended count across those two
  boundaries must be zero and the public cancellation fact is mandatory. It records the ABSENCE of a
  legacy concept, never a legacy truth, and it adds no load.
- **`api-dwell-anchored`**: every reported dwell must equal the game-time difference to its own
  same-session anchor EXACTLY, with no epsilon. That is a source fact, not an approximation: the
  adapter reads the clock separately for the tracker call and for the drain that stamps the fact
  (four reads per pair), but the binding is `GamePlayer.elapsedTime`, which the inspected build
  advances once per frame in `GamePlayer.Update` and otherwise writes only on load, while all reads
  of one pair happen synchronously inside the same patched frame - so the clock is constant across
  the pair and the subtraction is exact. A departure whose anchor is LATER than itself (a clock
  rollback) must report no dwell at all - unknown, not missing - and a reported dwell there fails.
  At least one strictly positive anchored dwell is required, and the receipt publishes the anchor,
  departure and dwell at full round-trip precision so a reader can redo the subtraction.

The API's own facts are the ground truth throughout; the legacy log is the compared artefact.

This comparison does not cover `RecoveredPlacement` or post-gate continuation; those have a separate phase. The tutorial exit remains source-attested and host-tested only. `RuntimeQualified=false`.

## Menu-only UI inspection

`Prepare -Scenario MissingApi -MenuInspection` selects an opt-in read-only main-menu census. It refuses gameplay/load probes and consumer combinations. The normal isolated launcher, source-file/preferences preservation and owned-process exit checks remain mandatory. Coordinate an exclusive native run with other sessions; a free-looking process list is not a lease.

The API-independent guard waits for the menu and two real-time settling seconds, verifies the inspected original game hash, then records the actual canvas hierarchy, anchors, scaler, input module, font assets and modal state. It does not click controls, change game objects or load a save. The bounded private `menu-inspection.txt` is covered by a versioned SHA-256 receipt; selection/provenance and receipt mismatches refuse the run. No screenshot or raw profile is published. This is source/UI-layout inspection, **not** acceptance of the Mods screen, catalog, networking or general UI readiness.
