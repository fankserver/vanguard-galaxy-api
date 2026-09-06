# Persistence identity decision (milestone 02)

This specifies the optional persistence service; it does not migrate existing consumers or implement storage yet. Runtime session IDs are never persistence keys.

## Four separate scopes

- Installation settings contain configuration, not campaign progression. Account-wide progression requires a separately named, explicitly enabled provider feature; campaign restoration never falls back to it.
- A campaign has an opaque generated ID. New game always starts a new campaign. No filename, player name or process-global value identifies a campaign.
- A snapshot association records campaign ID, snapshot ID, a canonical local slot key, exact vanilla `.save` payload-file SHA-256 and the digest of the complete mod-state generation. Associations are immutable.
- Runtime session identity authorizes access only during that load/play attempt. It does not survive restart or identify a save snapshot.

## Decision: no vanilla metadata changes

Do not inject identifiers or arbitrary mod data into vanilla JSON. The adapter remains observational and vanilla files stay independently loadable. Match sidecar generations by exact vanilla bytes and a canonical local slot key, not modification time. Persist IDs in service-owned metadata only. Canonical slot resolution belongs to the storage adapter; keys must already be normalized (including Windows case/alias handling), because policy compares them ordinally and case-sensitively. SHA-256 strings use lowercase hexadecimal. Hashes and IDs are opaque to consumers.

The identity hashes the complete `.save` file only, not its companion version-metadata `.meta` file. Both writes are still required for a successful lifecycle save outcome. Metadata is not treated as a snapshot discriminator. The subsequent [schema inspection](persistence-schema.md) found no unique-per-save identifier in either wrapper; the displayed timestamp is filesystem metadata. No timestamp uniqueness is assumed.

This has an unavoidable limitation: two different mod states can accompany byte-identical vanilla files. Without an identifier embedded in vanilla, a later rollback to identical bytes cannot be distinguished. Therefore an existing slot+vanilla-hash association MUST NOT be replaced with different mod state or campaign identity. Reject the conflicting commit, retain the original association and diagnostics, and pause affected persistence. The coordinator/store records conflict evidence and blocks subsequent exact-hash restoration until explicit recovery, rather than silently selecting the older mod state. This also refuses legitimate same-campaign forward progression when mod state changes but the `.save` bytes do not: the newer mod state is not committed, and persistence pauses rather than silently dropping the conflict. This availability cost is intentional. Do not claim this solves arbitrary identical-byte rollback. A future embedded-identity design requires a separate compatibility decision and reviewed migration.

## Cases

| Operation | Policy |
|---|---|
| New game | Fresh campaign; no automatic inherited progression even when a slot is reused. |
| Save-as | Inherit the active campaign, create an association for the destination slot and written hash; conflicting existing associations are refused. |
| Autosave rotation/reuse | Each local slot+`.save`-payload hash is independent; `.meta` is not hashed; retain prior immutable generations so restoring older bytes can recover only their matching state. |
| Ordinary reload | Restore only a valid association matching both slot and exact vanilla hash. |
| Rollback | Select the generation matching the slot and exact `.save` bytes, never latest campaign progress or the `.meta` file. Missing generations mean no known state, not permission to apply future state. |
| Copy/import without explicit adoption | A different slot never automatically inherits another slot's progression. Start isolated, without using the foreign metadata. |
| Explicit fork/import | Both actual vanilla-payload and imported mod-generation digests must match source metadata; use a fresh campaign ID and a distinct destination slot; explicit action, never inferred from a filename. |
| Missing metadata | Fresh isolated identity with no restored state. Distinguish absence from unreadable/corrupt/unsupported metadata: those are blocked, not empty fallbacks. |
| Provider removal | Does not authorize dropping unknown state or rewriting the generation; schema/removal policy is specified separately. |

Retaining generations costs disk space. No automatic pruning is promised in this milestone: explicit deletion loses that recovery history. A vanilla write and generation publication are not a cross-file transaction. The coordinated storage milestone must stage owner snapshots, publish only after matching successful writes, and diagnose incomplete commits.

## Evidence boundary

Pure identity-policy fixtures must demonstrate cross-slot isolation, exact rollback selection, explicit import, absence versus invalid metadata, and refusal of ambiguous identical-byte progression. They are not filesystem crash tests or native storage qualification. Those belong to the schema and coordinated persistence deliveries. External CustomMission progression and archived TravelJournal are not migration targets.
