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

## Runtime rules that compile clean and fail in game

These are repeat offenders. The compiler and the host tests cannot catch any of them,
so check them by reading, every time you write plugin or example code.

- **Keep every runtime string ASCII.** The game's `pixel16` font has no glyph for `—`,
  `→` or `…`. TextMeshPro silently replaces each with a space and logs a warning *per
  render*, so one em dash in a HUD row becomes a visible gap plus continuous log noise.
  Use `-`, `->` and `...` in anything the game displays or logs. Comments, READMEs and
  commit messages are unaffected.
- **Acquire instance-authenticated providers in `Start()`, never `Awake()`.** BepInEx
  assigns `Chainloader.PluginInfos[].Instance` only *after* `Awake()` returns
  (`Chainloader.Start()` calls `AddComponent`, which runs `Awake`, and assigns
  `.Instance` on the next line). World, story, bars, items and recipes resolve the
  caller against exactly that entry, so acquiring in `Awake()` returns null and
  silently registers nothing. Dungeon/HUD/panel registrations use a plain plugin-id
  string and are exempt, but use one consistent rule anyway.
- **Declare world content before a session exists.** `RegisterPocketSystem`,
  `RegisterResourceSite`, `RegisterCombatSite` and friends return `NotReady` once
  `CurrentSession != null`. Registering lazily on first use — a HUD click, a gameplay
  callback — is always too late, and the later `Create...` call then returns null with
  no explanation. Declare in `Start()`; create occurrences lazily.
- **Never discard a status result.** `WorldStatus`, `DungeonContentResult`,
  `StoryRegistrationResult`, `BarResult` and the rest report refusals that are
  otherwise invisible; a swallowed `NotReady` turns into a null two steps later and
  costs a whole debugging round. Log the status, including the success case, so a run
  can be verified positively rather than by absence of a warning.
- **Asset identifiers are prefab names, not display names.** Item ids come from
  `Resources/Items` prefab names (`SalvageCarbon`), not material words (`Carbon`) or
  market display names (`Synthblood Pack`). Verify an id against game data before using
  it as a default; never invent one.
- **Examples stay isolated.** An example may only touch content it authored itself. Do
  not register contextual actions on arbitrary observed targets, attach content to
  vanilla encounters, or take command leases on operations it did not create. If an
  example is about a mechanic, it must create its own instance of that mechanic rather
  than waiting for the player to find a suitable vanilla one, and it must clean up
  everything it made.
