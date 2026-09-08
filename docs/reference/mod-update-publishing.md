# Publishing mod updates

A loaded plugin with a direct hard or soft dependency on `vgmodapi` is listed
automatically. Optional `<plugin-guid>.vgmod.json` beside its DLL supplies metadata
and update participation; authors do not implement an executable update checker.
See [mod information](mod-information.md) for schemas, limits and supported hosts.

## Release identity

Use two-to-four numeric version segments, such as `1.2.3`. Missing components
compare as zero. Loader metadata does not support SemVer suffixes or a leading `v`.
The assembly version, `BepInPlugin` version, and advertised feed version must agree.
The feed's plugin ID and channel must match the installed sidecar. Release labels
are separate from the numeric loader version.

The [UpdateParticipant example](https://github.com/fankserver/vanguard-galaxy-api/tree/main/examples/UpdateParticipant)
shows compiled metadata and packaging. Replace its placeholder repository URLs
before distributing it. Never bundle the API, BepInEx, Unity or game assemblies.

## Discovery URLs

Stable packages may use:

```text
https://github.com/<owner>/<repo>/releases/latest/download/update.json
```

GitHub's latest release excludes prereleases. Experimental packages can instead
use a rolling prerelease asset:

```text
https://github.com/<owner>/<repo>/releases/download/updates-experimental/update.json
```

Keep that discovery release a prerelease. Its feed must link to the already-public
versioned release, not the rolling discovery page. Publish and verify the archive
before advancing discovery. Keep public release artifacts immutable; corrections
require a new version, not changed bytes under an advertised version.

An available release is not proof of API, game or save compatibility. Validate the
GUID, channel and version before publishing; test missing/invalid feeds and an
installed version ahead of the advertised version without changing player settings.
