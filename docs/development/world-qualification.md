# Restricted world qualification tooling

These Windows PowerShell helpers support the **empty Combat** profile only. They do not qualify broader owned-world content, replace the control matrix, or authorize native execution. See [probe scope](../../tools/world-probe-scope.md) and [world contracts](../reference/world-content.md).

## Authority and inputs

Before real preparation or execution, obtain maintainer authorization for the exact reviewed artifacts, selected disposable save fixtures and filesystem operations. Before a run or archive operation, hold the externally coordinated exclusive native lease. `-ExclusiveLeaseConfirmed` records an operator assertion; it does not acquire or transfer that lease. These helpers never release it.

Use a fresh numbered directory directly under the current user's local Temp directory. Source directories must be unlinked and disjoint from that destination. No helper grants approval based on hashes it just observed. An independent approval step must pin the completed run record's SHA-256 before launch validation.

The supplied authorization file binds the run identity, expiry and game/API/Core/Abstractions/guard/two-author/runner hashes. The plugin input directory supplies exactly those seven selected DLLs to the sandbox; reference DLLs and raw profiles must never be committed or published.

## Preparation and approval

Dot-source `tools/qualification-world-prepare.ps1` and call `New-WorldQualificationSandbox` with `Root`, `GameDirectory`, `PluginDirectory`, `SaveA`, `SaveB`, `AuthorizationPath` and `RunId`. It copies the two explicitly selected fixtures, not their surrounding profiles. Fixture provenance and suitability remain operator responsibilities. Preparation retains partial failures and refuses an existing destination.

The externally approved JSON record uses schema `world-empty-run-v1` and exactly these fields:

- `root`, `gameDirectory`, `runId`, `phase` (`create` or `cold`), `reviewedHead`;
- `authorizationSha256`;
- `gameInventory`, `saveInventory`, `stateInventory`: relative-path maps of directory/file-hash entries, with permitted resource-junction targets recorded in the game map;
- `process`: `fileName`, `workingDirectory`, `arguments`, and exact `environment` map;
- `preservationRoots`: the freshly derived source plugins/config, current-profile Saves, and any external configured production API state root;
- `timeoutSeconds`: integer from 1 through 3600;
- `creationEvidenceSha256`: empty for creation, externally approved creation-acceptance artifact hash for cold mode.

Preparation observations can inform independent review, but launch validation must consume already-approved expectations, not regenerate them from current inputs. `Read-WorldApprovedRun` checks a bounded, strictly decoded buffer against the supplied digest. `Assert-WorldRunPreflight` compares current inputs and the fixed process description against that record. Production-root comparison occurs again when preservation is captured.

## Running a phase

Dot-source `tools/qualification-world-run.ps1`; call `Invoke-WorldQualificationPhase` with the approved scope, record path/digest, matching timeout and explicit lease confirmation. It refuses existing game processes and stale output paths. It persists preferences recovery information and production preservation observations before rechecking inputs and starting the exact validated process description.

The child environment is minimized, not a complete filesystem sandbox: USERPROFILE and APPDATA still identify the real profile. Save redirection, preferences restoration, preservation checks and the external lease remain necessary. The supervisor retains uncertain owned-process state rather than searching for or killing processes by name.

Acceptance requires all of the following:

1. Established process exit without timeout, launcher kill, startup/cleanup failure or missing process identity; only exit `0` or the inspected self-termination code `-1` is allowed.
2. Preferences restoration and unchanged production inventories.
3. A fresh bounded runner result beginning with exact `PASS`.
4. Independent phase and paired-generation receipts.

Only then is `world-phase-accepted.json` emitted, binding the run, reviewed head, approval digest, process PID/start time and generation-receipt hash. Any failure retains evidence. Pending cleanup retains the process object in exception data for the supervising caller; do not release the lease or restore shared preferences until cleanup is established.

## Cold continuity

After successful creation, independently approve its acceptance artifact digest. `Save-WorldCreationArchive` in `tools/qualification-world-archive.ps1` preserves root evidence, owned game files, disposable Saves/state/temp and inventory metadata under `creation-evidence`. Resource junctions are recorded, never traversed into the archive. Only verified output copies are cleared. The original temporary directory is retained too.

The canonical `Saves/qa-owned-world.save`, its state directory and `world-created-generation.txt` remain in place. Archive copies are evidence, **not renamed load fixtures**. Retain externally stored approval records as well.

Supply fresh cold authorization and independently approve the cold input record. Cold validation binds the archived creation acceptance by digest and requires a different run identity and process PID/start-time pair. It also verifies unchanged generation authority against the canonical slot.

## Resource cleanup

After establishing that the owned game has exited and recovery/preservation has completed, `Disconnect-WorldQualificationResources` in `tools/qualification-world-cleanup.ps1` disconnects only the inspected resource junctions using nonrecursive deletion. It requires the external lease confirmation and refuses a live game process. Regular files, private evidence and the source installation are retained. Partial disconnection requires inspection rather than automatic retry; this is not a general sandbox deletion command or lease release.

## Limits and recovery

These are point-in-time checks under exclusive filesystem control. They do not prove continuous integrity, resource-content immutability, hardlink isolation, filesystem confidentiality, crash-durable archival or full Unity compatibility. Partial archives refuse automatic retry; inspect and retain them rather than deleting evidence. There is no automatic production-file rollback or safe-uninstall claim.

The synthetic Windows suites exercise helper behavior with fake files, processes or registry adapters. The shared profile suite additionally uses a disposable registry key and benign child processes. None of those tests establishes native world qualification. Full creation/cold execution, control cases, broader content, ordered story/bar references and actual consumers remain required.
