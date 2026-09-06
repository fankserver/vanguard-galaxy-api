# Immutable generation storage (coordinator foundation)

Internal storage primitive only: lifecycle capture, public registration, consumer migration and native qualification are still pending. Calling Publish is not itself authorization to save; the later coordinator must call it only for a matching successful vanilla operation.

## Layout and publication

Use a dedicated owned root. Each canonical slot key maps to the first 32 hex characters of its SHA-256 directory; exact `.save` SHA-256 selects an immutable generation directory. Full local slot paths are not stored in manifests. Owner filenames use the first 32 hex characters of namespace hashes, never provider-supplied paths. Full slot digests and owner identities remain in the manifest: truncated-name collisions fail closed through identity checks or exclusive file creation, not silent reassignment. The constructor rejects roots whose longest generated filename would exceed 259 characters, even on non-Windows hosts. This avoids requiring long-path-aware Windows/Mono operation; choose a shorter non-linked root when refused.

Each generation contains a versioned, digested manifest and opaque owner envelopes. The manifest pins campaign/snapshot identities and the digest of the complete ordered owner generation. Up to 32 owners are accepted, each bounded to the schema envelope maximum (1 MiB+128 bytes). Manifest reads are capped at 16 KiB; text fields are bounded before decoding. Unknown owner envelopes can be retained without installing their providers; interpreting them belongs to the schema/coordinator boundary.

Publish copies inputs, checks existing immutable identity, creates a unique sibling stage directory, writes/flushed owner files and manifest, then renames the directory to the final hash. It never overwrites an existing generation. A matching retry returns the original snapshot ID; different progression or campaign under the same slot+vanilla bytes is refused. An existing corrupt/unsupported generation is protected, not a missing/empty fallback.

## Interruption and recovery

A failure before rename leaves an ignored stage directory; normal loading only reads the final directory. A failure after rename may report an error even though the complete generation is present: a matching retry discovers it idempotently. Interrupted stages and old generations are retained for inspection, not automatically removed. Disk usage can grow; explicit cleanup loses that recovery evidence.

Same-filesystem directory rename is the publication boundary; individual files are flushed. This does not guarantee power-loss durability of directory metadata or atomicity with vanilla files. If vanilla succeeded but publication did not, report incomplete persistence and pause affected mutation; never silently attach future state. Filesystem recovery here is tested by explicit interruption exceptions, not actual power cuts.

Only a missing final directory returns absence. Read errors, missing files within a published directory, schema faults, digest mismatches and unexpected entries throw and preserve data. Links in paths are refused, including ancestor junctions and OneDrive reparse-point placeholders. Root selection must use a writable non-linked local location; a junctioned Steam library is not automatically acceptable. Benign OS files such as desktop.ini or .DS_Store also trigger the strict layout block; manually removing only the stray entry restores an otherwise intact generation. Never remove required generation files as a recovery shortcut. This is a trusted single-process owned directory, not a defense against an adversary racing path replacements; no cross-process locking or directory-fsync guarantee is claimed.

## Verification boundary

Real temporary-directory fixtures exercise pre/post-publication interruption, idempotent recovery, conflicting retries, slot rotation/save-as/rollback, corrupt/future published data, extra entries, defensive copies and link refusal. They do not prove Windows/Mono rename semantics or coordinated vanilla save timing. Issue #8 remains open until integration and two actual consumer pilots validate those paths.
