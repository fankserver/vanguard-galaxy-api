# Agent instructions

Read and follow [CONTRIBUTING.md](CONTRIBUTING.md) before making changes. It is the
canonical source for contributor rules, design constraints and documentation policy.

- Design for modders first. Follow [Design for modders](CONTRIBUTING.md#design-for-modders):
  expose simple gameplay objects, operations and naturally actionable events, not
  internal scheduling, session or mutation-gate machinery. Ordinary mod code should
  express gameplay intent without coordination boilerplate. Internal correctness
  alone does not make a public API acceptable.
- Deliver working API functionality with focused correctness tests. No qualification,
  preparation/research/analysis workstreams, acceptance matrices or probe-only PRs.
  Follow the finite completion rule in CONTRIBUTING.md; do not invent new gates.
- Implement directly in the assigned parent session. Assign the GitHub task to the
  authenticated account; commit, push and open a PR for the coherent feature/fix.
- Use the read-only asynchronous `reviewer` for that completed change. Retain its
  context for substantive fix deltas; coordinate concurrent writers through intercom.
- Run relevant Makefile checks and fix real failures. Do not add generic council,
  coverage, native qualification or mandatory CI-wait gates. Never claim a failed
  review or a failing test passed.
- Squash-merge when authorized once the functional scope, relevant tests and source
  review are complete. Deployment and release publication remain separate actions.

## Plugin code: compiles clean, fails in game

Neither the compiler nor the host tests catch these.

- Keep runtime strings ASCII. `pixel16` has no `—`, `→` or `…`; TextMeshPro substitutes
  a space and warns per render.
- Acquire instance-authenticated providers (world, story, bars, items, recipes) in
  `Start()`. `PluginInfos[].Instance` is assigned after `Awake()` returns, so acquiring
  in `Awake()` returns null and registers nothing.
- Declare world content before a session exists; `Register*` returns `NotReady` once
  `CurrentSession != null`. Create occurrences lazily, declare early.
- Never discard a status result. A swallowed refusal reappears as an unexplained null
  later. Log it, success included, so a run is verified positively.
