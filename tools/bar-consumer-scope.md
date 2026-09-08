# Controlled consumer bar composition

Prepare a fresh isolated `Full` sandbox with `-BarConsumerManifest <private JSON path>`, the usual two native fixture saves, and the exact reviewed API `BuildRoot`/`BuildRevision`. This profile cannot be combined with other probes. Run and clean up with the standard launcher; it selects the runner branch from prepared provenance and requires the exact consumer receipt and bounded capacity receipt.

The manifest has:

- `plugins`: exactly three entries named `VGAnima`, `VGTTS`, and `VanguardGalaxy.CustomMission`. Each contains `name`, an absolute DLL `path`, its lowercase SHA-256 `sha256`, and the full lowercase source `revision`.
- `jsonRuntime`: absolute Newtonsoft.Json DLL `path` and lowercase `sha256`.
- `ttsTools`: absolute physical directory containing the real TTS tools/model bundle. Links are refused; the bundle is copied, never linked into production. Files and directories are sealed in prepared provenance and rechecked before and after execution.

Consumer revisions are build-receipt attestations, not independently inferred source identities. Before preparation, retain clean exact-head build/review evidence associating each consumer revision with the supplied DLL hash. Preparation checks assembly names and hashes; run validation additionally checks BepInEx identities and the API/runner's embedded revision. No third-party tools or models are committed to this repository.

LLM requests and speech are disabled and rechecked. TTS still loads its real controller and finalized-roster subscriber, but this profile does **not** qualify voice generation, warming, eviction or delayed synthesis. The Anima input is a controlled ore-gathering offer with empty narrative lines, passed through its real finalization/assignment path, not an HTTP response.

The profile invokes CustomMission's real lazy Foundation builder with a disposable native mission whose source is the fixture station. It then temporarily selects that real Foundation POI and system on the captured player, synchronously exercises bar composition, and independently attempts to restore each player field and the permission setting in nested cleanup. No frame advances or save is requested inside the scoped context. This is not natural arrival, docking, campaign progression or Foundation UI acceptance.

Capacity preparation uses at most eight real forced bar refreshes and reads retained vanilla count from native JSON. It never deletes vanilla rows to manufacture a seat. The checks require exactly four exclusive Foundation contacts, an actual retained Anima offer denied specifically by that exclusive owner, exact finalized TTS object membership, and one admitted Anima contact after Foundation's exclusive permission is revoked. The source-revision baseline must be reviewed before any native run; source tests alone do not qualify this profile.
