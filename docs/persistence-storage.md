# Immutable generation storage (coordinator foundation)

Internal storage primitive and lifecycle coordinator engine only: runtime wiring, public registration, consumer migration and native qualification are still pending. Calling Publish is not itself authorization to save; the later coordinator must call it only for a matching successful vanilla operation.

## Layout and publication

Use a dedicated owned root. Each canonical slot key maps to the first 32 hex characters of its SHA-256 directory; exact `.save` SHA-256 selects an immutable generation directory. Full local slot paths are not stored in manifests. Owner filenames use the first 32 hex characters of namespace hashes, never provider-supplied paths. Full slot digests and owner identities remain in the manifest: truncated-name collisions fail closed through identity checks or exclusive file creation, not silent reassignment. The constructor rejects roots whose longest generated filename would exceed 259 characters, even on non-Windows hosts. This avoids requiring long-path-aware Windows/Mono operation; choose a shorter non-linked root when refused.

Each generation contains a versioned, digested manifest and opaque owner envelopes. The manifest pins campaign/snapshot identities and the digest of the complete ordered owner generation. Up to 32 owners are accepted, each bounded to the schema envelope maximum (1 MiB+128 bytes). Manifest reads are capped at 16 KiB; text fields are bounded before decoding. Unknown owner envelopes can be retained without installing their providers; interpreting them belongs to the schema/coordinator boundary.

Publish copies inputs, checks existing immutable identity, creates a unique sibling stage directory, writes/flushed owner files and manifest, then renames the directory to the final hash. It never overwrites an existing generation. A matching retry returns the original snapshot ID; different progression or campaign under the same slot+vanilla bytes is refused. An existing corrupt/unsupported generation is protected, not a missing/empty fallback.

## Interruption and recovery

A failure before rename leaves an unpublished stage directory; normal loading never treats it as a complete generation. An unmatched hash in a slot containing history, stages or intent evidence is blocked, not classified as fresh empty state. A failure after rename may report an error even though the complete generation is present: a matching retry discovers it idempotently. Interrupted stages and old generations are retained for inspection, not automatically removed. Disk usage can grow; explicit cleanup loses that recovery evidence.

Same-filesystem directory rename is the publication boundary; individual files are flushed. This does not guarantee power-loss durability of directory metadata or atomicity with vanilla files. If vanilla succeeded but publication did not, report incomplete persistence and pause affected mutation; never silently attach future state. Filesystem recovery here is tested by explicit interruption exceptions, not actual power cuts.

Only a missing final directory in a slot without any persistence evidence returns absence. Read errors, missing files within a published directory, schema faults, digest mismatches and unexpected entries throw and preserve data. Links in paths are refused, including ancestor junctions and OneDrive reparse-point placeholders. Root selection must use a writable non-linked local location; a junctioned Steam library is not automatically acceptable. Benign OS files such as desktop.ini or .DS_Store also trigger the strict layout block; manually removing only the stray entry restores an otherwise intact generation. Never remove required generation files as a recovery shortcut. This is a trusted single-process owned directory, not a defense against an adversary racing path replacements; no cross-process locking or directory-fsync guarantee is claimed.

## Lifecycle coordinator engine

The internal coordinator subscribes before any session starts. Providers register fixed owner codecs/capture/restore callbacks before a session; dynamic provider removal is not yet implemented. All access is main-thread-only. Callbacks must affect only their own in-memory state and must not mutate vanilla operations or block.

At SessionStarting, hash the canonical load source; at PlayerReady, verify it has not changed and read only the exact generation. New games or absent generations receive fresh campaign identities. Corrupt published generations block the load instead of restoring empty state. Each owner's schema/restore failure independently denies that owner's mutation; opaque inactive/unsupported owner bytes are retained.

Capture begins only after restoration has completed for a matching session and SaveStarted operation (including PlayerReady saves before GameplayInitialized). This is after vanilla snapshot serialization, not a pre-serialization hook. Before capture, persist a per-operation slot intent even for a blocked/unrestored session. A successful vanilla write without a published generation retains that intent, so later loads cannot reinterpret it as fresh empty state. Skipped operations clear only their matching intent. Failed operations retain it because the payload write may have succeeded before metadata failed. Exact valid generations remain loadable alongside retained intent evidence. Encode defensive snapshots; failed active-owner capture aborts the whole candidate and pauses mutation, rather than relabeling older bytes as current. A later save can retry capture. SaveFailed/SaveSkipped discard their candidate without publication. SaveSucceeded must match operation, current session and canonical destination; only then hash actual destination bytes and publish.

Mutation permission additionally requires no lifecycle callback dispatch, no pending saves and no publication fault. Publication faults pause all mutation until a successful publication or a new load. Schema/restore failures require another load; they do not become empty success. Original opaque bytes of inactive owners may be carried forward because they were never granted mutation. Reentrant session replacement during restore/capture abandons the old attempt. Invalidation/disposal clears pending candidates without a quit flush.

Registered plus retained owner namespaces must fit the 32-owner union limit; overflow reports owner-union-limit rather than dropping unknown owners.

If the filesystem cannot record even the pre-write intent for a previously unseen destination, vanilla cannot be stopped by this observer and no durable lineage proof can be guaranteed. The current session pauses, but a later process cannot distinguish wholly absent metadata from first use. Deleting all storage has the same limitation. This is an explicit unresolved cross-file availability/durability boundary, not a safe-empty recovery promise; native qualification and installation guidance must expose it. No vanilla metadata or save suppression is introduced.

This engine does not validate an active game installation or provide a public API yet. It trusts the supplied canonical-path and file-hash adapters and the lifecycle hub; runtime adapters must enforce actual path/version/thread constraints. No account-wide fallback or implicit legacy-sidecar import exists.

## Verification boundary

Real temporary-directory fixtures exercise pre/post-publication interruption, idempotent recovery, conflicting retries, slot rotation/save-as/rollback, corrupt/future published data, extra entries, defensive copies and link refusal. They do not prove Windows/Mono rename semantics or coordinated vanilla save timing. Issue #8 remains open until integration and two actual consumer pilots validate those paths.
