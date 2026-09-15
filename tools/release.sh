#!/usr/bin/env bash
# Run from a clean checkout of the release tag with trusted local game references.
set -euo pipefail
tag=${1:?Usage: tools/release.sh v1.2.3}
[[ "$tag" =~ ^v([0-9]+\.[0-9]+(\.[0-9]+){0,2})$ ]] || { echo 'Expected a numeric release tag' >&2; exit 1; }
version=${BASH_REMATCH[1]}
[[ $(git rev-parse HEAD) == "$(git rev-parse "refs/tags/$tag^{commit}")" ]] || { echo 'Check out the release tag first' >&2; exit 1; }
[[ -z $(git status --porcelain) ]] || { echo 'Release checkout must be clean' >&2; exit 1; }
# Release creation and feed publication are deliberately not part of the build.
[[ $(gh release view "$tag" --json isPrerelease --jq .isPrerelease) == true ]] || { echo 'Create the GitHub prerelease first' >&2; exit 1; }
make check-local CONFIGURATION=Release RELEASE_VERSION="$version" RELEASE_CHANNEL=stable
make release-archive CONFIGURATION=Release RELEASE_VERSION="$version" RELEASE_CHANNEL=stable
archive="VGModAPI-$version-stable.zip"
(cd artifacts && sha256sum "$archive" > "$archive.sha256")
# No --clobber: a repeated or partial upload fails safely rather than replacing assets.
gh release upload "$tag" "artifacts/$archive" "artifacts/$archive.sha256"
