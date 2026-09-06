# Mission transition foundation

This is the host-tested contract/state-machine foundation for issue #11, not a wired native mission capability. Adapter integration, journal/provider adoption and native traces remain required. No consumer should infer availability from the presence of these types.

`IMissionEvents` supplies immutable `MissionTransition` payloads with an observation sequence and `MissionSnapshot`. A snapshot separates the mission definition from the live instance, copies objective tags without collapsing them to one archetype, and records whether acceptance was actually observed. The producer owns no persistent history.

## Identity and restoration

The foundation's instance GUID is stable for an observed live occurrence within its session. Different objects sharing a story definition remain different instances; re-acceptance after an observed removal starts another occurrence even when an object is reused. Restored missions do not fabricate acceptance. Reset invalidates pending observations and identity mappings, so rollback/load never silently becomes a new acceptance. Cross-load correlation is not supplied by this foundation; validated continuity and missing-history handling remain integration requirements, not an ordinal/name-based matching promise. Weak identity keys do not retain every departed vanilla mission indefinitely.

## Proof and ordering

Internal observation scopes reserve logical order at entry, then accept verified state changes rather than successful method returns. An adapter must provide facts from actual membership, failure flags, archive count changes and reward-associated removal. A completion proof must come from a reward claim that actually removed the mission; direct removal with a completed flag is not by itself reward completion. Forced completion still requires that removal proof. No-op acceptance or ineligible reward claims emit nothing.

Nested scopes publish only after the outer operation settles, in reserved logical order. Thus an outer failure precedes its replacement's acceptance, and completion precedes nested archive notification. Repeated signals of the same kind for a live occurrence are deduplicated. `Removed` is a membership observation, distinct from a terminal outcome. The adapter must suppress misleading abandonment during a failure and witness transient insertion if a mission fails/removes itself during acceptance; final before/after membership alone can miss that insertion.

All access is main-thread-only. Subscribers must not mutate in-progress vanilla operations. Their exceptions are isolated. Session replacement during notification stops stale delivery; disposal drops queued observations. Snapshot objective tags are a copied set, not a reference to mutable vanilla lists. Detailed mission construction/native-object access is not exposed by this foundation; any later version-sensitive escape hatch must be explicit and separately guarded.

## Inspected source constraints

On inspected game0.8.2.3, `AddMissionWithLog(Mission,bool)` may return without insertion for a duplicate story ID. `ClaimRewards(bool)` can reject eligibility, or remove after rewards when eligible/forced. Removal nests archive for story missions; failure sets its flag before notifications and may remove the old mission and force-add a replacement. There is no inspected per-live-instance GUID on the basic Mission object. These observations guide the pending adapter, not a claim of completed runtime coverage.
