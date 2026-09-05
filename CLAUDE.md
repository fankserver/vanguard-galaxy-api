# VGModAPI contributor guidance

Unofficial Vanguard Galaxy API, first milestone: core session/load/save lifecycle. Read `docs/lifecycle-contract.md` and `docs/compatibility.md` before modifying hooks. `docs/implementation-plan.md` records scope and follow-up work.

- Work in this independent Git repository; do not modify sibling mods as part of API work without authorization.
- Use `make build`, `make test`, `make check-bindings`; package with `make package CONFIGURATION=Release`.
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
