#!/usr/bin/env python3
"""Surity-based in-game E2E driver for VGModAPI.

Alternative to the self-contained EWTest driver. Instead of a bespoke in-game harness, this
delegates running-in-the-game, the batchmode launch and streamed results to the proven Surity
framework for Unity/BepInEx mods (`surity <game-exe>`). This PR exists to evaluate that
third-party-dependency tradeoff against the dependency-free self-contained variant (evaluated
in the sibling PR). Each driver is self-contained so the two can be compared in isolation.

This driver owns the VGModAPI-specific parts Surity does not: disposable-save isolation (real
saves are never touched) and gating on Surity's exit code (0 = all tests passed). Surity prints
its own console results and exits non-zero on failure.

Opt-in developer tool: never in public CI, never shipped. Surity's batchmode+handshake support
for the specific game still needs validation on the game machine (`make e2e-surity`).
"""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


class E2EError(Exception):
    pass


class SaveGuardError(E2EError):
    """Raised when the real save tree appears modified after a run."""


def tree_manifest(root: Path) -> dict:
    manifest: dict = {}
    if not root.is_dir():
        return manifest
    for dirpath, dirnames, filenames in os.walk(str(root)):
        dirnames.sort()
        dirpath_p = Path(dirpath)
        for name in sorted(filenames):
            full = dirpath_p / name
            if full.is_symlink():
                continue
            rel = str(full.relative_to(root))
            manifest[rel] = hashlib.sha256(full.read_bytes()).hexdigest()
    return manifest


def assert_real_saves_unchanged(before: dict, real_dir: Path) -> None:
    after = tree_manifest(real_dir)
    if after == before:
        return
    changed = [k for k in sorted(set(before) | set(after)) if before.get(k) != after.get(k)]
    raise SaveGuardError(
        "real save tree changed during the e2e run (%d path(s): %s) - aborting. "
        "The harness must run against the disposable profile only." % (len(changed), ", ".join(changed[:20]) or "(empty)"))


class DisposableSaveProfile:
    """Isolates the disposable profile and verifies the real saves are untouched."""

    def __init__(self, real_dir, workspace):
        self.real_dir = Path(real_dir)
        self.workspace = Path(workspace)
        self.path: Path | None = None
        self._backup: Path | None = None
        self._before: dict = {}

    def __enter__(self) -> "DisposableSaveProfile":
        if not self.real_dir.is_dir():
            raise E2EError("save dir does not exist: %s" % self.real_dir)
        self._before = tree_manifest(self.real_dir)
        self.workspace.mkdir(parents=True, exist_ok=True)
        self._backup = Path(tempfile.mkdtemp(prefix="vgmodapi-surity-", dir=str(self.workspace)))
        self.path = self._backup / "disposable"
        self.path.mkdir()
        return self

    def __exit__(self, exc_type, exc, tb):
        if self.path is not None and self.path.is_dir():
            shutil.rmtree(self.path, ignore_errors=True)
        if self._backup is not None and self._backup.is_dir():
            shutil.rmtree(self._backup, ignore_errors=True)
        assert_real_saves_unchanged(self._before, self.real_dir)
        return False


def build_command(surity, game_dir, executable="VanguardGalaxy.exe"):
    return [surity, str(Path(game_dir) / executable)]


def gate_exit(exit_code: int) -> int:
    """Surity: 0 = all passed, non-zero (1 = test failures, 2 = exit requested) = fail."""
    return 0 if exit_code == 0 else 1


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        prog="e2e-surity", description="Surity-based in-game E2E driver for VGModAPI (opt-in dev tool).")
    parser.add_argument("--game-dir", default=os.environ.get("GAME_DIR", ""),
                        help="Vanguard Galaxy install dir (default: $GAME_DIR)")
    parser.add_argument("--executable", default="VanguardGalaxy.exe")
    parser.add_argument("--surity", default=os.environ.get("SURITY", "surity"),
                        help="Surity runner: a `surity`/`dotnet surity` alias or path to Surity.exe")
    parser.add_argument("--save-dir", default=None, help="real save directory to guard (never modified)")
    parser.add_argument("--runtime-dir", default=None, help="workspace dir for the disposable profile")
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--preview", action="store_true", help="print the launch command without running")
    args = parser.parse_args(argv)

    if not args.game_dir:
        parser.error("--game-dir is required (or GAME_DIR)")

    cmd = build_command(args.surity, args.game_dir, args.executable)
    print("e2e-surity: launch argv: %s" % " ".join(cmd))

    if args.preview:
        print("e2e-surity: --preview: wiring OK (Surity handles batchmode launch + streaming; game not run).")
        return 0

    if args.save_dir:
        runtime = Path(args.runtime_dir) if args.runtime_dir else Path(tempfile.mkdtemp(prefix="vgmodapi-surity-"))
        with DisposableSaveProfile(real_dir=args.save_dir, workspace=runtime) as profile:
            print("e2e-surity: disposable profile at %s" % profile.path)
            return gate_exit(_run_surity(cmd))
    return gate_exit(_run_surity(cmd))


def _run_surity(cmd):
    try:
        result = subprocess.run(cmd, timeout=900, capture_output=True, text=True)
    except FileNotFoundError:
        print("e2e-surity: Surity runner not found (%s). Install via `dotnet tool install Surity.CLI`."
              % " ".join(cmd[:1]), file=sys.stderr)
        return 1
    except subprocess.TimeoutExpired:
        print("e2e-surity: timed out waiting for Surity.", file=sys.stderr)
        return 1
    if result.stdout:
        print("--- Surity stdout ---")
        print(result.stdout)
    if result.stderr:
        print("--- Surity stderr ---", file=sys.stderr)
        print(result.stderr, file=sys.stderr)
    return result.returncode


if __name__ == "__main__":
    sys.exit(main())
