# Agent instructions

Read and follow [CONTRIBUTING.md](CONTRIBUTING.md) before making changes. It is the
canonical source for contributor rules, design constraints and documentation policy.

- Implement directly in the assigned parent session. Assign the GitHub task to the
  authenticated working account, commit and push completed chunks, and open a PR.
- Review through the read-only `reviewer` subagent, launched asynchronously.
  Establish an exact-head baseline and resume the same reviewer for deltas;
  coordinate through intercom/supervisor messaging.
- Address substantive findings and never claim a failed review completed. Use the
  project Makefile checks; generic Gemini/council/CI-coverage gates are not required.
- Squash-merge only when authorized, without closing untested runtime scope.
