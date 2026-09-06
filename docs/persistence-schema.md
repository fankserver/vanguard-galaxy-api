# Namespaced schema and recovery policy

Milestone02 schema delivery: a pure codec and migration boundary, not coordinated disk storage. The later coordinator must enforce the publication rules below. No existing consumer sidecar is automatically adopted or rewritten.

## Inspected vanilla format

For the currently inspected assembly, SaveCurrentState wraps Version and serialized Player; WriteVersionMetadata writes only Version to the companion `.meta`. SaveGameFile.Timestamp reads filesystem LastWriteTime. Neither wrapper provides a per-save unique identifier. Player state can change with gameplay, but no unique-per-write value is assumed. The identity policy therefore continues hashing complete `.save` bytes only and refusing ambiguous identical-byte state changes. These are concise findings from local source inspection, not redistributed decompiled bodies.

## Owner envelope

OwnerSchemaCodec uses only netstandard binary IO, UTF-8/ASCII and SHA-256; no JSON library is introduced or shipped. Providers own their payload formats and runtime dependencies. A provider using Newtonsoft must arrange one compatible runtime copy; the game is not assumed to supply it. System.Text.Json is not introduced given prior Unity/Mono faults. This codec is internal until the optional persistence service exposes its registration API.

Envelope v1: ASCII magic VGOS, one-byte envelope version, one-byte owner length, owner ASCII, little-endian Int32 provider-schema version, Int32 payload length, payload, then SHA-256 of all preceding bytes. SHA-256 detects corruption, not malicious authorship. Owner IDs are 1–64 lowercase ASCII letters/digits/dots/hyphens, beginning with a letter. They are namespaces, never filesystem paths. A coordinator must refuse duplicate registrations.

Envelope version, provider schema version, public API version and game-adapter version are independent. Schema versions are positive integers. Payloads are at most 1 MiB and envelopes at most payload limit+128 bytes. Read exact framing and refuse trailing bytes, invalid lengths, incorrect owner or digest. The storage reader must enforce the envelope bound before allocating/reading the entire file.

## Outcomes and migrations

- Missing input yields Missing, never fabricated successful data.
- Corrupt framing/digest/validation yields Corrupt.
- Newer envelope or provider schema yields Unsupported, with no downgrade attempt. The envelope-version discriminator is read before digest verification because future layouts may move the digest; damage to that byte can therefore report Unsupported rather than Corrupt. Both outcomes protect the source.
- Older data requires every explicitly registered n→n+1 migration; at most 64 steps; larger version gaps fail before callbacks run. Missing, throwing, oversized or invalid migrations yield MigrationFailed.
- Successful decoding yields Ready and a defensive payload copy; successful migration yields a candidate plus a migrated flag. It does not write files.

Source bytes, callback inputs and returned payloads are isolated by copying. One owner's failure does not poison another codec. Callbacks are trusted plugin code, not a sandbox: they must return promptly; this API cannot prevent a callback's unrelated filesystem/global side effects.

## Publication, retention and diagnostics

Never rename/delete a source merely because reading or migration failed. No automatic destructive quarantine. Retain immutable known-good generations; a diagnostic or optional copied quarantine artifact may reference the fault without replacing its source. Migration is an all-or-nothing in-memory candidate: publish only through the coordinator after its matching successful vanilla save, never during a read. Unrelated owners may be decoded independently, but publication must preserve unavailable/unknown owners rather than silently omit them.

Report owner, status and expected schema without payloads or raw provider exceptions. Corrupt/unsupported/migration-failed state blocks that owner's mutation/publication, not an empty fallback. Retention has no automatic expiry; manual deletion explicitly loses recovery. No cross-file atomicity is claimed. Crash staging, atomic replacement, owner registration ownership and actual consumer tests remain the coordinated persistence issue's work.

## Verification boundary

Deterministic fixtures cover missing/corrupt/future states, digest coverage, exact owner matching, chained migrations, mutating/throwing migrations, unrelated-owner success, missing steps, invalid output and allocation bounds. These are pure codec tests; filesystem recovery and native Mono consumer use are not claimed by this delivery.
