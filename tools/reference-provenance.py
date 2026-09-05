#!/usr/bin/env python3
"""Emit reference hashes/tool versions, never source paths or proprietary contents."""
import argparse
import hashlib
import json
import platform
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--game-dir", required=True)
parser.add_argument("--dotnet", default="dotnet")
parser.add_argument("--configuration", choices=("Debug", "Release"), required=True)
args = parser.parse_args()
root = Path(args.game_dir)
repository = Path(__file__).resolve().parent.parent
references = {
    "BepInEx.dll": root / "BepInEx/core/BepInEx.dll",
    "0Harmony.dll": root / "BepInEx/core/0Harmony.dll",
    "UnityEngine.dll": root / "VanguardGalaxy_Data/Managed/UnityEngine.dll",
    "UnityEngine.CoreModule.dll": root / "VanguardGalaxy_Data/Managed/UnityEngine.CoreModule.dll",
    "Assembly-CSharp.dll": root / "VanguardGalaxy_Data/Managed/Assembly-CSharp.dll",
}
def digest(path, label):
    try:
        with path.open("rb") as stream:
            return hashlib.file_digest(stream, "sha256").hexdigest()
    except OSError:
        raise SystemExit("Cannot hash local input/output: " + label) from None


hashes = {}
for name, path in references.items():
    if name != "Assembly-CSharp.dll":
        link = repository / "VGModAPI/lib" / name
        if not link.is_symlink() or link.resolve() != path.resolve():
            raise SystemExit("Reference link does not match requested installation: " + name)
    hashes[name] = digest(path, name)
package_hashes = {
    name: digest(repository / "artifacts/VGModAPI" / name, name)
    for name in ("VGModAPI.dll", "VGModAPI.Core.dll", "VGModAPI.Abstractions.dll")
}


def output(*command):
    return subprocess.check_output(command, text=True).strip()


print(json.dumps({
    "revision": output("git", "rev-parse", "HEAD"),
    "worktree_dirty": bool(output("git", "status", "--porcelain")),
    "dotnet_sdk": output(args.dotnet, "--version"),
    "requested_configuration": args.configuration,
    "python": platform.python_version(),
    "reference_source": "owner-provided local installation; no asset download or upload",
    "reference_sha256": hashes,
    "package_sha256": package_hashes,
    "evidence": "Current resolved reference links and package bytes; standalone reporting does not attest a prior compiler invocation.",
    "qualification": "reference identity only, not Unity qualification",
}, indent=2))
