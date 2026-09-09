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
