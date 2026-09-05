# Controlled Unity qualification runner

This is development tooling for issue #2, not part of the API distribution. Owner-approved test deployment is required. The owner's complete milestone acceptance remains a separate gate.

## Isolation

`tools/qualification.ps1` provisions a fresh Windows sandbox:

- Copies the launcher/Unity native runtime files and local BepInEx core.
- References the installed read-only game resources through directory junctions; does not redistribute them.
- Installs only VGModAPI, the observer example, and the opt-in qualification runner.
- Copies two provided saves into a separate `Saves` directory and creates synthetic future-version/corrupt fixtures there.
- Records hashes of files in the **real save directory as well as fixture directories**, even when the supplied fixtures are already copies. The runner checks the protected directory against vanilla's actual pre-redirection SavesPath.
- Records plugin SHA-256 hashes and an optional BuildRevision, refusing changed prepared binaries.
- Snapshots/restores the inspected title's shared PlayerPrefs registry key around Run. This is not full Windows-profile isolation: other persistent-profile writes are not redirected.

The runner refuses an unmarked sandbox or a different executable directory. It redirects both vanilla save-directory fields before testing and guards Store/Recall against non-sandbox destinations. Steam initialization is disabled in the isolated process to avoid achievements/stat changes or relaunching the installed game. The inspected `--fse-shim-applied` bootstrap marker prevents the game's fullscreen workaround from writing a registry entry and spawning an unowned child.

The game runs **windowed, not batch mode**: the inspected vanilla GlobalControls reads Mouse.current, which can be null in batch mode. This tooling is not a security sandbox against malicious plugins or paths. It intentionally omits other mods and does not qualify their coexistence. Use `-Action Cleanup` to unlink the three resource junctions non-recursively. It preserves private evidence and local copies; never recursively delete a sandbox while those junctions still exist.

## Build and prepare

Build from WSL/Linux with the installed reference paths available:

```sh
make package CONFIGURATION=Release
```

The solution builds `QualificationRunner.dll` but the API package allowlist excludes it. On Windows, run the self-authored PowerShell script from a **local** path if RemoteSigned treats a WSL UNC path as remote; do not disable machine execution policy.

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

`OriginalSaveDir` defaults to the inspected title's normal Windows save directory and must exist. It is independent of SaveA/SaveB. `GameDir` can override the installed game location. The game must not already be running. Every attempt requires a new directory; do not reuse failed evidence. The launcher bounds process lifetime (900 seconds by default, configurable via TimeoutSeconds) and cleans up only processes at that sandbox's exact executable path. Keep the process budget longer than the sequence's bounded per-stage waits.

## Evidence and coverage

Local output includes `events.tsv` (with failure detail/exception type when supplied by the API), `result.txt`, game/BepInEx logs, process/provenance receipts, and preservation markers. A PASS result is meaningful only together with successful save/prefs preservation and log inspection. **Treat every raw artifact as private**, including TSV details, stack traces, receipts, and registry snapshots: they may contain usernames, full paths, or preferences. Publish only reviewed, redacted excerpts.

The automated sequence checks two copied loads, session replacement, a manual save and roundtrip, ephemeral skip, exhausted retries through an intentionally blocked sandbox metadata filename, subscriber exception isolation/disposal, rejected future/corrupt fixtures, and recovery afterward. It uses vanilla GameManager.LoadGame rather than assuming an iterator factory completing means readiness. Session-id checks prevent stale-session acceptance; an unscaled two-second settle grace permits remaining startup work and time scale is not frozen for saving. This is a harness heuristic, not a universal world/UI-ready guarantee.

A failed check must remain failed until its cause is investigated. Record bootstrap/harness failures separately from reproduced API defects. No test edits the allowed game hash or claims all-world readiness.

Run `tools/tests/qualification.Tests.ps1` on Windows for synthetic-file tests of manifest coverage, the DLL allowlist, preloader configuration, provenance, path/reuse refusal, and junction cleanup. These tests do not launch Unity or exercise PlayerPrefs restoration. Follow-up hardening after qa-04 has not been rerun in Unity; the original startup blocker remains open.

Earlier qa-01 through qa-04 runs predated PlayerPrefs snapshots. Their save checks do **not** prove unchanged settings; display preferences may have changed and no before-snapshot exists to restore them reliably.

Still separate: new-game UI setup, a deliberately selected space/docked matrix, normal autosave rotation, successful retry recovery, unsupported game-hash behavior, UI acceptance, and multi-mod combinations. Do not set RuntimeQualified merely because the automated subset passes.
