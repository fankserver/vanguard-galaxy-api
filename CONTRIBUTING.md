# Contributing to VGModAPI

Use .NET SDK 10, GNU make, and Python 3.11+. Runtime libraries target
`netstandard2.1`; warnings are errors. Build and package targets need your local
game/BepInEx references; pure host tests do not. Commands and installation-path
overrides are defined in the [Makefile](Makefile).

## Changes and review

- Work in this independent repository; changes to sibling mods require separate authorization.
- Assign the task to your working account, use a branch and Conventional Commits,
  and open a PR for each delivery chunk. Address substantive review findings before
  delivery. Squash-merge only when authorized.
- Read the [lifecycle contract](docs/reference/lifecycle-contract.md) and
  [compatibility limits](docs/reference/compatibility.md) before modifying hooks.
- Run the relevant Makefile checks. Never run reference-bearing checks on untrusted
  PR code or upload game references or raw profiles to public CI.
- Deployment, release publication and native testing require explicit maintainer
  authorization and disposable/copied saves. Host tests and bounded native probes
  do not establish full in-game acceptance; keep untested scope explicit.
- Never commit or package game, Unity, BepInEx or Harmony reference DLLs, decompiled
  source, raw profiles or private receipts. Local references stay in ignored `VGModAPI/lib`.

## Implementation boundaries

Completed API modules initialize automatically. Enable switches are temporary for
unfinished modules and must be removed when their milestone closes—not merely
changed to default-on. Keep compatibility and dependency safety gates.

- Public contracts belong in Abstractions and must not expose vanilla/Unity types.
  Core and adapter internals are not a supported consumer API.
- Keep Harmony hooks in `VGModAPI/Patches`. Inspect original game semantics, not
  just signatures; never allow a new game hash without reinspection. Coroutine
  factory return is not completion, gameplay initialization is not all-world-ready,
  and a Store return is not proof of disk success.
- Observer failures must not suppress vanilla exceptions or break the game. Isolate
  subscribers individually and test stale sessions, nesting, retries, skips and disposal.
- Do not assume the game provides Newtonsoft.Json or introduce System.Text.Json
  without reevaluating Unity/Mono compatibility.

## API-owned content

Supported persistent API-owned content must save and reconstruct automatically.
Authors register definitions and declare lifetime/retention—not save hooks, codecs,
sidecars or restoration scheduling for API-owned fields. The generic save-data API
is for **additional custom mod data**.

- Use stable provider/plugin and local identifiers. Keep live occurrences distinct
  from definitions; namespaces prevent collisions, not malicious same-process access.
- Default persistent creation to saved content. Refuse creation when required
  persistence is unavailable; only explicitly transient content may omit persistence.
- Retain necessary progress, outcomes and choices in the save without requiring a
  journal mod. Keep optional narrative history separate; fail safely at retention limits.
- Preserve ownership and references across reload, new game, save-as, slot changes,
  rollback, failed/skipped writes, provider absence and supported migrations. Reuse
  safe vanilla serialization, without promising cross-file atomicity or executable serialization.

Observer/storage plumbing or consumer-owned save hooks do not establish API-managed
content persistence. Keep bespoke campaign and combat behavior in consumer mods.

## Documentation

Keep one source of truth: commands and filters in the Makefile/workflows, package
layout in `tools/release_archive.py`, public contracts in `docs/reference/`, and
contribution rules here. Developer tooling guides belong in `docs/development/`;
keep Markdown and navigation portable for future hosting.

Link rather than repeat mechanics. Simplify confusing interfaces before adding
explanations, while retaining necessary safety constraints and contract semantics.
Describe current behavior, supported compatibility and applicable constraints—not
development diaries, superseded decisions or historical reports. Keep private
execution evidence outside the repository. Supported migrations remain current
behavior; deprecated APIs need explicit replacement guidance.

Issues track work, not durable documentation. Do not reference issue numbers or
issue/milestone URLs in docs or runtime messages. State the current limitation or
link its canonical contract instead. Use precise terms: a provider ID or operation's
owning instance is a technical identity, not a claim of human approval or acceptance.
