# Agent instructions

Read [CONTRIBUTING.md](CONTRIBUTING.md) before making changes. It is the canonical
source for delivery, review, implementation and documentation rules. Read the
relevant [API contracts](docs/reference/README.md) for the behavior being changed.

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
