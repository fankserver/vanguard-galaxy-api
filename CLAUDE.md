# Agent instructions

Read [CONTRIBUTING.md](CONTRIBUTING.md) before making changes. It is the canonical
source for delivery, review, implementation and documentation rules. Read the
relevant [API contracts](docs/reference/README.md) for the behavior being changed.

## Execution

- Implement in the assigned parent session. Use the read-only asynchronous
  `reviewer` for the completed change and retain its context for substantive fixes.
- Coordinate concurrent writers through intercom and separate worktrees. Do not
  edit another session's uncommitted work.
- Follow the contribution guide's delivery checks. Do not add generic council,
  coverage targets or mandatory CI-wait gates. Never report a failing test or
  failed review as successful.

## Memory boundaries

- Keep project policies and durable technical facts in their canonical repository
  files; link to existing guidance rather than copying it into persistent memory.
- Project memory should normally contain only a pointer to these instructions.
  Reserve other memory for genuinely durable preferences not already documented.
- Do not store test counts, commit/PR histories, milestone status, temporary model
  choices or one-off authorizations as standing project rules. Use the current
  task, issue or PR for that context; do not publish private session records.
- Remove redundant or superseded memories once their useful content is documented.
  Preserve active task context until its owner has a durable handoff.
- A remembered approval is not permission for a new action. Follow the current
  request's scope; a draft requested for owner review must remain unmerged.
