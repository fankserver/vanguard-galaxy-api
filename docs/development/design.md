# Design constraints

Public contracts live in the [API reference](../reference/README.md); contribution
and delivery rules live in [CLAUDE.md](../../CLAUDE.md). Future work belongs in the
[roadmap](https://github.com/fankserver/vanguard-galaxy-api/issues/1).

## Automatic content persistence

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
