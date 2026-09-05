# Controlled Unity qualification runner

This is development tooling for issue #2, not part of the API distribution. Owner-approved test deployment is required. The owner's complete milestone acceptance remains a separate gate.

## Isolation

`tools/qualification.ps1` provisions a fresh Windows sandbox:

- Copies the launcher/Unity native runtime files and local BepInEx core.
- References the installed read-only game resources through directory junctions; does not redistribute them.
- Installs only VGModAPI, the observer example, and the opt-in qualification runner.
- Copies two provided saves into a separate `Saves` directory and creates synthetic future-version/corrupt fixtures there.
- Records hashes of all files in the source save directories and checks them again after the owned process exits.

The runner refuses an unmarked sandbox or a different executable directory. It redirects both vanilla save-directory fields before testing and guards Store/Recall against non-sandbox destinations. Steam initialization is disabled in the isolated process to avoid achievements/stat changes or relaunching the installed game. The inspected `--fse-shim-applied` bootstrap marker prevents the game's fullscreen workaround from writing a registry entry and spawning an unowned child.

The game runs **windowed, not batch mode**: the inspected vanilla GlobalControls reads Mouse.current, which can be null in batch mode. This tooling is not a security sandbox against malicious plugins or paths. It intentionally omits other mods and does not qualify their coexistence. Do not use junction cleanup commands that recursively follow the game-resource junctions.

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
  -BuildRoot "C:\path\to\vanguard-galaxy-api"

.\qualification.ps1 -Action Run `
  -SandboxRoot "$env:TEMP\VGModAPI-qa-unique-run"
```

`GameDir` can override the installed game location. The game must not already be running. Every attempt requires a new directory; do not reuse failed evidence. The launcher bounds process lifetime and cleans up only processes at that sandbox's exact executable path.

## Evidence and coverage

Local output includes `events.tsv`, `result.txt`, isolated game/BepInEx logs, a process receipt, and `original-saves-unchanged.txt`. A PASS result is meaningful only together with successful original-save verification and inspection of relevant logs. Do not publish private saves or the manifest containing local save paths.

The automated sequence checks two copied loads, session replacement, a manual save and roundtrip, ephemeral skip, exhausted retries through an intentionally blocked sandbox metadata filename, subscriber exception isolation/disposal, rejected future/corrupt fixtures, and recovery afterward. It uses vanilla GameManager.LoadGame rather than assuming a save iterator factory completing means the game is ready.

A failed check must remain failed until its cause is investigated. Record bootstrap/harness failures separately from reproduced API defects. No test edits the allowed game hash or claims all-world readiness.

Still separate: new-game UI setup, a deliberately selected space/docked matrix, normal autosave rotation, successful retry recovery, unsupported game-hash behavior, UI acceptance, and multi-mod combinations. Do not set RuntimeQualified merely because the automated subset passes.
