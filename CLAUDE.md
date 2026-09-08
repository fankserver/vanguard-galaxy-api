# VGModAPI contributor guidance

Unofficial Vanguard Galaxy API: lifecycle, mod save data, optional mission/travel/story services, and mod information. Read `docs/reference/lifecycle-contract.md` and `docs/reference/compatibility.md` before modifying hooks. Contributor constraints are defined here; GitHub issues track work, not durable documentation.

## Current-state-only content

Every repository file must describe current behavior, supported compatibility or an applicable constraint. This applies to documentation, comments, runtime messages, examples and contributor instructions. Do not retain development diaries, run-by-run reports, superseded decisions, replaced draft names or historical policy explanations, including in a separate repository archive. Explain the current invariant and its reason instead of recounting the fix.

Keep current testing limits explicit: controlled native evidence is not full in-game acceptance or proof for an untested source revision. Detailed execution history and private receipts stay outside the repository. Supported save schemas and migrations remain current behavior, not historical narrative. Retain superseded public APIs only with explicit deprecation and replacement guidance; do not silently break consumers.

Use precise terms: a mod/provider identifier and an operation's owning instance are technical concepts; human approval and acceptance are not properties of those identities. Implementers apply this policy to every change, and reviewers check it alongside correctness.

Keep one source of truth: commands and filters belong in the Makefile/workflows,
package layout in `tools/release_archive.py`, public contracts in `docs/reference/`,
and contributor guidance in `docs/development/`. Link rather than restate mechanics.
Simplify confusing interfaces before adding explanatory prose; retain necessary
safety constraints and contract semantics. Documentation indexes provide navigation,
not duplicate summaries. Keep Markdown portable for future documentation hosting. Do not use issue numbers or
issue/milestone links as documentation or runtime-message references; state the current
contract or limitation directly, or link its canonical documentation.

## Implementation and delivery

- Work in this independent Git repository; do not modify sibling mods as part of API work without authorization.
- Assign a GitHub issue to the authenticated working account when starting it. Work task by task on a branch, commit with Conventional Commits, push completed changes, and create a PR for each delivery chunk.
- Implement directly in the assigned parent session. Use read-only native asynchronous subagents for review with GPT-6, low thinking (`openai-codex/gpt-6-astra:low`). Keep and resume the same reviewer session for exact-head deltas after establishing a reviewed baseline; coordinate through intercom/supervisor messaging. Address substantive findings and never claim a failed review completed.
- Use the project make checks and the persistent GPT-6 reviewer; generic Gemini/council/CI-coverage PR gates are not required. Squash-merge delivery chunks when authorized without closing untested runtime scope.
- Complete in-game acceptance testing is a separate milestone gate. Keep unexercised or failing runtime gates explicit rather than closing qualification issues on host tests alone.
- Use `make build`, `make test`, `make check-bindings`; package with `make package CONFIGURATION=Release`. `make check-local` runs the local reference/package chain defined in `Makefile`; never run that reference-bearing path on untrusted PR code. Never send local game references or raw profiles to public CI.
- Runtime libraries target netstandard2.1; tests require .NET SDK 10. Warnings are errors.
- Never commit or package game, Unity, BepInEx, or Harmony reference DLLs. `VGModAPI/lib` holds local ignored symlinks.
- Public contracts belong in Abstractions and must not expose vanilla/Unity types. Core and adapter internals are not a supported consumer API.
- Keep Harmony hooks in `VGModAPI/Patches`. Observer failures must not suppress vanilla exceptions or break the game. Isolate subscriber failures individually.
- Gate semantics on inspected original game code, not just signatures. The runtime currently rejects uninspected hashes. Never update the allowed hash without reinspection.
- Coroutine factories returning are not completion. GameplayInitialized is deliberately narrower than all-world-ready. A Store return is not proof of successful disk writing.
- Maintain tests for stale session signals, nesting/reentrancy, retries, skips, and subscriber disposal.
- Do not assume the game provides Newtonsoft.Json; do not introduce System.Text.Json without reevaluating known Unity/Mono compatibility issues.
- No deployment, release, or destructive save testing without explicit project-maintainer authorization. Always distinguish passing host tests from actual Unity qualification.
- Do not redistribute decompiled game source. Preserve concise findings and member mappings in docs instead.

## API-owned content

Supported persistent API-owned content must save and reconstruct automatically.
Mod authors register definitions and declare lifetime/retention; they must not
supply save hooks, codecs, sidecars or restoration scheduling for API-owned fields.
The generic save-data API is for **additional custom mod data**.

- Identity is a stable provider/plugin ID plus local ID, never a display name or
  invented string prefix. Repeated live instances remain distinct from definitions.
  Namespaces prevent collisions, not malicious same-process access.
- Persistent creation defaults to saved content. If required persistence is
  unavailable, refuse creation rather than silently creating unsaved content.
  Explicitly transient effects, UI handles and observations need no permanent record.
- Retain necessary mission progress, completion/outcomes and supported choices
  within the save, without requiring a journal plugin. Optional narrative history
  is separate. Retention limits must fail safely rather than truncate progression.
- Preserve ownership and references through reload, new game, save-as, slot changes,
  rollback, failed/skipped writes, provider absence and supported migrations.
  Reuse safe vanilla serialization where appropriate; do not promise cross-file
  atomicity or serialization of executable behavior.

Observer/storage plumbing alone does not satisfy these requirements. Consumer
integrations retaining their own save hooks are not API-managed content persistence.
Keep bespoke campaign and combat behavior in consumer mods rather than the API.
