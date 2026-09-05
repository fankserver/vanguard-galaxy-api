# VGModAPI contributor guidance

Unofficial Vanguard Galaxy API, first milestone: core session/load/save lifecycle. Read `docs/lifecycle-contract.md` and `docs/compatibility.md` before modifying hooks. `docs/implementation-plan.md` records scope and follow-up work.

- Work in this independent Git repository; do not modify sibling mods as part of API work without authorization.
- Assign a GitHub issue to the authenticated working account when starting it. Work task by task on a branch, commit with Conventional Commits, push completed changes, and create a PR for each delivery chunk.
- The owner requests a read-only `claude -p` review with Fable 5.1 for each implementation. Address substantive findings; do not silently substitute a different model or claim a failed review completed.
- For this project, the owner waived generic Gemini/council/CI-coverage PR gates. Use the project make checks and requested Fable review; do not impose unrelated readiness prerequisites. Squash-merge delivery chunks when authorized without closing untested runtime scope.
- The owner will perform complete acceptance testing after each milestone. Keep unexercised or failing runtime gates explicit rather than closing qualification issues on host tests alone.
- Use `make build`, `make test`, `make check-bindings`; package with `make package CONFIGURATION=Release`. `make check-local` runs the local reference/package chain; see `docs/checks.md`. Never send local game references or raw profiles to public CI.
- Runtime libraries target netstandard2.1; tests require .NET SDK 10. Warnings are errors.
- Never commit or package game, Unity, BepInEx, or Harmony reference DLLs. `VGModAPI/lib` holds local ignored symlinks.
- Public contracts belong in Abstractions and must not expose vanilla/Unity types. Core and adapter internals are not a supported consumer API.
- Keep Harmony hooks in `VGModAPI/Patches`. Observer failures must not suppress vanilla exceptions or break the game. Isolate subscriber failures individually.
- Gate semantics on inspected original game code, not just signatures. The runtime currently rejects uninspected hashes. Never update the allowed hash without reinspection.
- Coroutine factories returning are not completion. GameplayInitialized is deliberately narrower than all-world-ready. A Store return is not proof of successful disk writing.
- Maintain tests for stale session signals, nesting/reentrancy, retries, skips, and subscriber disposal.
- No serializer or general persistence layer is implemented. Do not assume the game provides Newtonsoft.Json; do not introduce System.Text.Json without reevaluating known Unity/Mono compatibility issues.
- No deployment, release, or destructive save testing without owner authorization. Always distinguish passing host tests from actual Unity qualification.
- Do not redistribute decompiled game source. Preserve concise findings and member mappings in docs instead.
