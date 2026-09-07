# Author metadata and release publication

## Listing versus update participation

A loaded plugin declaring a direct hard or soft dependency on `vgmodapi` is listed automatically. There is no registration callback, networking implementation or author-supplied executable checker. Optional `<plugin-guid>.vgmod.json` beside the loaded DLL supplies description/project/update URLs and a channel; it never supplies installed version. See [mod-information.md](mod-information.md) for the exact schemas, limits, consent and host policy.

`examples/UpdateParticipant` is a compile/package example. Its `ExampleVersion` MSBuild property generates both the assembly version and the constant used by `BepInPlugin`. Release labels are separate from that numeric value. With lawful local BepInEx/Unity compile references available:

```sh
make example-update-package CONFIGURATION=Release EXAMPLE_VERSION=1.2.3
```

The resulting `artifacts/UpdateParticipant.zip` contains only `UpdateParticipant.dll` and its metadata. It does **not** redistribute `VGModAPI.Abstractions.dll`, BepInEx, Unity or game assemblies. The `example/mod` repository URLs are explicit placeholders: replace them with the author's own repository before distributing the example. The package target validates compiled metadata and runs publication in dry-run mode; it does not access or modify GitHub.

## Numeric release identity

Use two-to-four numeric loader segments, such as `1.2`, `1.2.3` or `1.2.3.4`. Missing components compare as zero. Arbitrary SemVer suffixes, leading `v` inside loader metadata and label-to-number conversion are unsupported. Official release tags are exactly `v<version>` for stable or `v<version>-experimental` for experimental. The tag's separate suffix chooses the channel; it does not become part of the loader version.

The `tools/ReleaseMetadata` console tool uses Mono.Cecil to read the **packaged** DLL without executing it or resolving proprietary dependencies. It requires exactly one matching BepInPlugin GUID and parity between its version, assembly version and the supplied numeric release version. It parses the byte-matched packaged sidecar through the shared metadata validator, requiring the same GUID and channel and a supported source. Official packages must use the channel's discovery URL in the publication repository; other authors may select any supported GitHub feed URL, but must keep its identity/channel consistent. It then validates the generated JSON through the same feed parser used by the checker. A mismatched package cannot announce the proposed version.

## Explicit publication pipeline

`.github/workflows/release.yml` is owner-dispatched against an existing reviewed tag. `publish=false` validates only. `publish=true` is explicit release authorization. The trusted self-hosted runner must have the `linux` and `vgmodapi-release` labels, the pinned .NET SDK, Python, `gh`, and lawful local compile references. There is no PR or branch-push release trigger; never expose this runner to untrusted PR jobs. No proprietary references, saves or native screenshots are uploaded.

The workflow builds/checks the selected tag and packages its DLLs. Local invocation has the same explicit boundary:

```sh
make release-archive CONFIGURATION=Release RELEASE_VERSION=1.2.3 RELEASE_CHANNEL=stable
python3 tools/publish_update.py --repo fankserver/vanguard-galaxy-api \
  --tag v1.2.3 --plugin vgmodapi --version 1.2.3 --channel stable \
  --archive artifacts/VGModAPI-1.2.3-stable.zip \
  --assembly artifacts/VGModAPI/VGModAPI.dll
# Add --publish only with release authorization.
```

Validation first checks the archive's exact owned-file allowlist and byte equality to the package, then generates the feed from compiled BepInPlugin metadata. A checked development build is not advertised merely because it exists locally.

Publication order is:

1. Create/reuse a draft versioned release, without marking it latest.
2. Upload the archive and its SHA-256 checksum. Existing different bytes are rejected; public artifacts are immutable.
3. Make the versioned release public without promoting latest; verify the public archive bytes.
4. Upload and read back its generated `update.json`.
5. Only then advance discovery: mark a stable release latest, or replace the `update.json` asset on the public prerelease `updates-experimental`.

A failure before step 5 leaves the previous advertised feed in place. Reruns verify and reuse identical artifacts, finish missing stages, and recover a partially created experimental discovery draft. Different immutable assets, channel mismatches and backwards version movement are rejected. The same numeric version cannot point discovery at a different release. GitHub asset replacement may briefly return 404; the checker reports failure/stale last-success, never an invented successful update. Rollback requires an intentional new version/policy decision, not silently publishing an older feed.

The workflow's concurrency group serializes publishers. Do not run a second standalone publisher concurrently or edit reserved discovery assets manually; the GitHub asset API does not provide an atomic compare-and-swap for this protocol. Authentication/network lookup errors stop publication rather than being treated as missing releases. Credentials are supplied through normal `gh` authentication or `GH_TOKEN`, never feed/player configuration.

## Stable and experimental discovery

Stable author packages may use:

```
https://github.com/<owner>/<repo>/releases/latest/download/update.json
```

GitHub redirects this stable asset URL through its release-asset infrastructure; the checker validates every HTTPS hop against its exact supported hosts. `/releases/latest` **excludes prereleases**.

Experimental packages instead use the explicit rolling prerelease asset:

```
https://github.com/<owner>/<repo>/releases/download/updates-experimental/update.json
```

Its feed links to the already-public, immutable versioned release, not to the rolling discovery page. `tools/local_update_metadata.py` selects the official packaged source/channel from `RELEASE_CHANNEL`; the source sidecar carries no working-tree version. The reserved discovery release must stay a prerelease so it cannot displace stable latest. Author forks must update repository URLs and their owned-file package allowlist before adopting this pipeline.

## Author test cases

- **Missing source:** omit `updateUrl` from a disposable package's author metadata. The normal loader row remains; no check is offered.
- **Failed check:** use an unavailable/invalid feed on a supported host in a disposable package and explicitly confirm a check. It must report failed/invalid, not up to date. Do not alter player configuration to publish feeds.
- **Installed ahead:** use a valid older feed with a newer numeric installed example build. It must report installed-ahead and not recommend downgrade.
- **Wrong GUID/channel/version:** validate generated feeds and intentionally mismatched packaged assemblies; generation must fail before publication.
- **Partial publication:** fake-remote tests inject failures at archive, public-release, feed and discovery stages and exercise retries without announcing missing artifacts.

Pure tests validate metadata and ordering without proprietary assets. Native Mono/TLS/redirect behavior and full end-to-end acceptance remain separate qualification in #80. An available release is never proof of API/game/save compatibility.
