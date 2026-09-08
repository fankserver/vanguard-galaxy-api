# Agent instructions

Read and follow [CONTRIBUTING.md](CONTRIBUTING.md) before making changes. It is the
canonical source for contributor rules, design constraints and documentation policy.

- Implement directly in the assigned parent session. Assign the GitHub task to the
  authenticated working account, commit and push completed chunks, and open a PR.
- Use read-only native asynchronous reviewers with GPT-6, low thinking
  (`openai-codex/gpt-6-astra:low`). Establish an exact-head baseline and resume the
  same reviewer for deltas; coordinate through intercom/supervisor messaging.
- Address substantive findings and never claim a failed review completed. Use the
  project Makefile checks; generic Gemini/council/CI-coverage gates are not required.
- Squash-merge only when authorized, without closing untested runtime scope.
