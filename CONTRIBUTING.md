# Contributing to VGModAPI

Use .NET SDK 10, GNU make, and Python 3.11+. Runtime libraries target
`netstandard2.1`; warnings are errors. Build and package targets need your local
game/BepInEx references; pure host tests do not. Commands and installation-path
overrides are defined in the [Makefile](Makefile).

## Delivery

Finish working API features, not a testing or evidence program. Keep a short,
finite list of missing behavior for the assigned milestone and implement coherent
feature chunks. Tests verify those behaviors or reproduce actual defects.

- No qualification campaigns, native acceptance matrices, preparation/research/
  analysis workstreams, probe/receipt infrastructure or one-scenario test PRs.
- Read existing code or game bindings only as needed to implement the behavior;
  do not turn that inspection into a separate deliverable or prerequisite project.
- Add focused API correctness tests with the feature. Retain save-data integrity,
  ownership, callback isolation and unsupported-build refusal tests. Do not build
  tests of qualification machinery or expand an exhaustive scenario matrix.
- Run the relevant checks for the completed chunk; repeat affected checks after
  fixes. Review the coherent change once and resume for substantive defect fixes,
  rather than restarting a full review for every small edit.
- When the required functionality and its relevant tests pass, complete the task
  or milestone. Do not invent another evidence gate. Record actual missing
  behavior as follow-up work; optional extra coverage is not a blocker.
- Test results describe the tested behavior, not universal correctness. Removing
  qualification work does not permit weakening runtime safety or losing saves.

## Changes and review

- Work in this independent repository; changes to sibling mods require separate authorization.
- Assign the task to your working account, use a branch and Conventional Commits,
  and open a PR for a coherent feature or fix, including its tests. Address
  substantive review findings before delivery. Squash-merge only when authorized.
- Read the [lifecycle contract](docs/reference/lifecycle-contract.md) and
  [compatibility limits](docs/reference/compatibility.md) before modifying hooks.
- Run the relevant Makefile checks. Never run reference-bearing checks on untrusted
  PR code or upload game references or raw profiles to public CI.
- Deployment, release publication and manual game/save experiments require
  separate maintainer authorization. Do not modify real saves during development.
  They are not part of the default feature-delivery or merge process.
- Never commit or package game, Unity, BepInEx or Harmony reference DLLs, decompiled
  source, raw profiles or private receipts. Local references stay in ignored `VGModAPI/lib`.

## Implementation boundaries

Completed API modules initialize automatically. Enable switches are temporary for
unfinished modules and must be removed when their milestone closes—not merely
changed to default-on. Keep compatibility and dependency safety gates.

- Use established game terminology for domain concepts and plain language in
  user-facing text. Prefer "save data" or "save/load" over internal coordination
  terminology; keep implementation vocabulary out of onboarding.
- Prefer typed events for lifecycle/state changes and snapshots for current state,
  rather than requiring consumers to poll for transitions. See the
  [service contracts](docs/reference/service-contracts.md) for delivery semantics.
- Public game contracts belong in Abstractions and must not expose vanilla/Unity
  types. The optional `VGModAPI.Unity` assembly provides typed Unity UI integration
  without exposing vanilla components; non-UI consumers do not depend on it.
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
