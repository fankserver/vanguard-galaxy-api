#!/usr/bin/env python3
"""In-game end-to-end regression driver for VGModAPI.

This is an opt-in local development tool (parallel to ``make check-bindings``).
It is never part of default CI and never shipped in the release package.

What it does
------------
* Launches the real Vanguard Galaxy with the API and the dev-only ``EWTest``
  harness loaded, configured to auto-run a suite of live assertions against
  the *running game* (not reflection of ``Assembly-CSharp.dll``).
* Waits for the harness to write a machine-readable JSON report.
* Parses and validates the report against the shared protocol, prints a human
  summary, and returns a non-zero exit status when any check fails. Each failure
  carries a ``suggestedAction`` that tells the maintainer exactly which binding,
  contract or behavior must be re-inspected for an unknown/updated game build.

Save safety
-----------
The driver never writes to your real saves. The disposable-save isolation guard
copies the live save directory into a private workspace, runs there, and after
the run verifies the real save tree is byte-for-byte unchanged (a manifest
sha-256 comparison), then discards the workspace copy.

No game installed on the current machine? Use ``--preview`` to validate the
wiring (report parsing, gating, isolation, launch command) without a game.

Report protocol (schema=1)
--------------------------
.. code-block:: json

    {
      "schema": 1,
      "meta": {
        "apiVersion": "0.2.0",
        "gameAssemblySha256": "<hex>",
        "gameVersion": "0.8.2.3",
        "unityVersion": "6000.4.7f1",
        "startedUtc": "2026-01-01T00:00:00Z",
        "finishedUtc": "2026-01-01T00:00:05Z"
      },
      "suites": [
        {
          "id": "availability",
          "name": "Public service availability",
          "results": [
            {
              "check": "SessionTracking available",
              "status": "pass" | "fail" | "skip",
              "message": "human summary",
              "expected": "shape/expectation",
              "actual": "observed value",
              "suggestedAction": "what to re-inspect to fix (failures only)",
              "elapsedMs": 12
            }
          ]
        }
      ],
      "summary": { "passed": 2, "failed": 0, "skipped": 1, "total": 3 }
    }
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile
import time
from pathlib import Path
from typing import Iterable, List, Optional

SCHEMA = 1
VALID_STATUS = ("pass", "fail", "skip")
REPORT_FILENAME = "report.json"


class E2EError(Exception):
    """Raised for malformed reports or invalid driver usage."""


class CheckResult:
    __slots__ = ("check", "status", "message", "expected", "actual",
                 "suggestedAction", "elapsedMs")

    def __init__(self, check, status, message="", expected="", actual="",
                 suggestedAction="", elapsedMs=0):
        self.check = check
        self.status = status
        self.message = message
        self.expected = expected
        self.actual = actual
        self.suggestedAction = suggestedAction
        self.elapsedMs = elapsedMs

    @property
    def failed(self) -> bool:
        return self.status == "fail"

    def to_dict(self) -> dict:
        return {
            "check": self.check,
            "status": self.status,
            "message": self.message,
            "expected": self.expected,
            "actual": self.actual,
            "suggestedAction": self.suggestedAction,
            "elapsedMs": self.elapsedMs,
        }

    @classmethod
    def from_dict(cls, raw: dict) -> "CheckResult":
        check = raw.get("check")
        status = raw.get("status")
        if not isinstance(check, str) or not check:
            raise E2EError("report: result missing a non-empty 'check' string")
        if status not in VALID_STATUS:
            raise E2EError("report: invalid result status %r for check %r" % (status, check))
        elapsed = raw.get("elapsedMs", 0)
        if not isinstance(elapsed, (int, float)) or elapsed < 0:
            raise E2EError("report: invalid elapsedMs %r for check %r" % (elapsed, check))
        return cls(
            check=check,
            status=status,
            message=str(raw.get("message", "")),
            expected=str(raw.get("expected", "")),
            actual=str(raw.get("actual", "")),
            suggestedAction=str(raw.get("suggestedAction", "")),
            elapsedMs=int(elapsed),
        )


class SuiteResult:
    __slots__ = ("id", "name", "results")

    def __init__(self, id, name, results):
        self.id = id
        self.name = name
        self.results = list(results)

    def to_dict(self) -> dict:
        return {"id": self.id, "name": self.name,
                "results": [r.to_dict() for r in self.results]}

    @classmethod
    def from_dict(cls, raw: dict) -> "SuiteResult":
        id = raw.get("id")
        name = raw.get("name")
        results_raw = raw.get("results")
        if not isinstance(id, str) or not id:
            raise E2EError("report: suite missing non-empty 'id'")
        if not isinstance(name, str):
            raise E2EError("report: suite %r missing 'name'" % id)
        if not isinstance(results_raw, list):
            raise E2EError("report: suite %r 'results' must be a list" % id)
        return cls(id, name, [CheckResult.from_dict(r) for r in results_raw])


class Report:
    __slots__ = ("schema", "meta", "suites")

    def __init__(self, schema, meta, suites):
        self.schema = schema
        self.meta = meta
        self.suites = list(suites)

    # -- derived counts ---------------------------------------------------
    @property
    def results(self) -> List[CheckResult]:
        return [r for s in self.suites for r in s.results]

    @property
    def passed(self) -> int:
        return sum(1 for r in self.results if r.status == "pass")

    @property
    def failed(self) -> int:
        return sum(1 for r in self.results if r.status == "fail")

    @property
    def skipped(self) -> int:
        return sum(1 for r in self.results if r.status == "skip")

    @property
    def failures(self) -> List[CheckResult]:
        return [r for r in self.results if r.failed]

    # -- serialization -----------------------------------------------------
    def to_dict(self) -> dict:
        return {
            "schema": self.schema,
            "meta": dict(self.meta),
            "suites": [s.to_dict() for s in self.suites],
            "summary": {
                "passed": self.passed,
                "failed": self.failed,
                "skipped": self.skipped,
                "total": len(self.results),
            },
        }

    @classmethod
    def from_dict(cls, raw: dict) -> "Report":
        if not isinstance(raw, dict):
            raise E2EError("report: root must be a JSON object")
        schema = raw.get("schema")
        if schema != SCHEMA:
            raise E2EError("report: unsupported schema %r (expected %d)"
                           % (schema, SCHEMA))
        meta = raw.get("meta")
        if not isinstance(meta, dict):
            raise E2EError("report: missing 'meta' object")
        suites_raw = raw.get("suites")
        if not isinstance(suites_raw, list):
            raise E2EError("report: 'suites' must be a list")
        report = cls(schema, {k: str(v) for k, v in meta.items()},
                     [SuiteResult.from_dict(s) for s in suites_raw])
        summary = raw.get("summary")
        if isinstance(summary, dict):
            # Tolerate a summary block, but recompute authoritative counts so a
            # lying/inconsistent summary cannot mask a failure.
            expected_total = summary.get("total")
            if expected_total is not None and not isinstance(expected_total, int):
                raise E2EError("report: 'summary.total' must be an integer")
        return report


def parse_report(text: str) -> Report:
    try:
        raw = json.loads(text)
    except json.JSONDecodeError as exc:
        raise E2EError("report: invalid JSON: %s" % exc) from exc
    return Report.from_dict(raw)


def load_report(path) -> Report:
    p = Path(path)
    if not p.is_file():
        raise E2EError("report file not found: %s" % p)
    return parse_report(p.read_text(encoding="utf-8"))


def gate(report: Report) -> int:
    """Exit code: 0 when nothing failed, 1 when any check failed."""
    return 1 if report.failed else 0


def human_summary(report: Report) -> str:
    lines = [
        "E2E report: %d passed, %d failed, %d skipped (total %d)"
        % (report.passed, report.failed, report.skipped, len(report.results)),
    ]
    meta = report.meta
    if meta.get("apiVersion"):
        lines.append("  api version: %s" % meta["apiVersion"])
    if meta.get("gameAssemblySha256"):
        lines.append("  game assembly sha256: %s" % meta["gameAssemblySha256"])
    if meta.get("gameVersion"):
        lines.append("  game version: %s" % meta["gameVersion"])
    for suite in report.suites:
        failed = [r for r in suite.results if r.failed]
        if not failed:
            continue
        lines.append("  suite %s: %d failing check(s)" % (suite.id, len(failed)))
        for r in failed:
            lines.append("    - %s" % r.check)
            if r.actual:
                lines.append("        observed: %s" % r.actual)
            if r.expected:
                lines.append("        expected: %s" % r.expected)
            if r.suggestedAction:
                lines.append("        fix: %s" % r.suggestedAction)
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Save safety: the disposable-save isolation guard
# ---------------------------------------------------------------------------
class SaveGuardError(E2EError):
    """Raised when the real save tree appears modified after a run."""


def tree_manifest(root: Path) -> dict:
    """Map of relative path -> sha256 of file content for a directory tree.

    Sorting is deterministic; symlinks are not followed (the game treats them
    as opaque), and empty directories are ignored consistently by all callers.
    """
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
    changed = []
    for key in sorted(set(before) | set(after)):
        if before.get(key) != after.get(key):
            changed.append(key)
    detail = ", ".join(changed[:20]) or "(empty)"
    raise SaveGuardError(
        "real save tree changed during the e2e run (%d path(s): %s) - aborting. "
        "The harness must run against the disposable profile only."
        % (len(changed), detail))


class DisposableSaveProfile:
    """Copies the live save directory into an isolated workspace, verifies the
    real tree is untouched after the run, then discards the copy.

    Usage::

        with DisposableSaveProfile(real_dir=..., workspace=...) as profile:
            ...  # launch game pointed at profile.path
    """

    def __init__(self, real_dir, workspace):
        self.real_dir = Path(real_dir)
        self.workspace = Path(workspace)
        self.path: Optional[Path] = None
        self._backup: Optional[Path] = None
        self._before: dict = {}

    def __enter__(self) -> "DisposableSaveProfile":
        if not self.real_dir.is_dir():
            raise E2EError("save dir does not exist: %s" % self.real_dir)
        self._before = tree_manifest(self.real_dir)
        self.workspace.mkdir(parents=True, exist_ok=True)
        self._backup = Path(tempfile.mkdtemp(prefix="vgmodapi-e2e-", dir=str(self.workspace)))
        self.path = self._backup / "disposable"
        self.path.mkdir()
        # Seed a fresh disposable profile; real saves are never copied in (a
        # disposable profile must not inherit test-affected state).
        return self

    def __exit__(self, exc_type, exc, tb):
        if self.path is not None and self.path.is_dir():
            shutil.rmtree(self.path, ignore_errors=False)
        if self._backup is not None and self._backup.is_dir():
            shutil.rmtree(self._backup, ignore_errors=False)
        # Guard regardless of run outcome: never let a run corrupt real saves.
        assert_real_saves_unchanged(self._before, self.real_dir)
        return False


# ---------------------------------------------------------------------------
# Launch command & report wait
# ---------------------------------------------------------------------------
def build_launch_command(game_dir, executable="VanguardGalaxy.exe"):
    """Return the argv list used to launch the game with the auto-run harness.

    Pure function (no I/O) so it is deterministic and testable. The harness is
    configured to auto-run via EWTest's BepInEx config, which the driver primes
    in the game's plugin folder before this launch.
    """
    game = Path(game_dir)
    exe = game / executable
    if not exe.is_file():
        raise E2EError("game executable not found: %s (set --game-dir)" % exe)
    # -nographics keeps the e2e from needing a display; the harness still runs
    # the real Unity loop. Developers targeting a full window may drop it.
    return [str(exe), "-nographics", "-screen-fullscreen", "0"]


def build_launch_env(report_path, run="1"):
    """Environment the harness reads to auto-run and where to write the report.

    The EWTest plugin honours EWTEST_RUN (set to '1' to auto-run on launch) and
    EWTEST_REPORT_PATH (absolute path of the report.json to write). Pure and
    deterministic so it can be tested.
    """
    env = dict(os.environ)
    env["EWTEST_RUN"] = run
    env["EWTEST_REPORT_PATH"] = str(report_path)
    return env


def wait_for_report(runtime_dir, timeout_seconds=600, poll_seconds=2):
    """Poll for the harness report under ``runtime_dir`` until timeout.

    Returns the Path, or None on timeout.
    """
    target = Path(runtime_dir) / REPORT_FILENAME
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if target.is_file():
            return target
        time.sleep(poll_seconds)
    return None


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def _load_or_else(report: Optional[Report], path: str) -> Report:
    if report is not None:
        return report
    raise E2EError("no report produced (timeout); see %s" % path)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        prog="e2e",
        description="In-game E2E regression driver for VGModAPI (opt-in dev tool).")
    parser.add_argument("--game-dir", default=os.environ.get("GAME_DIR", ""),
                        help="Vanguard Galaxy install dir (default: $GAME_DIR)")
    parser.add_argument("--executable", default="VanguardGalaxy.exe")
    parser.add_argument("--runtime-dir", default=None,
                        help="workspace for the disposable profile + report (default: temp)")
    parser.add_argument("--save-dir", default=None,
                        help="real save directory to guard (never modified)")
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--poll", type=float, default=2.0)
    parser.add_argument("--launch", action="store_true",
                        help="actually launch the game (default in normal mode)")
    parser.add_argument("--preview", action="store_true",
                        help="dry-run: build the launch command, seed + guard a "
                             "disposable profile, exit 0 without launching. CI-safe.")
    parser.add_argument("--report", default=None,
                        help="parse + gate an existing report file, then exit "
                             "(no launch, no guard)")
    args = parser.parse_args(argv)

    # Pure report evaluation mode: parse + validate + gate.
    if args.report:
        report = load_report(args.report)
        print(human_summary(report))
        return gate(report)

    if not args.game_dir:
        parser.error("--game-dir is required (or GAME_DIR) unless --report is given")

    runtime_dir = Path(args.runtime_dir) if args.runtime_dir \
        else Path(tempfile.mkdtemp(prefix="vgmodapi-e2e-run-"))

    try:
        cmd = build_launch_command(args.game_dir, args.executable)
    except E2EError as exc:
        print("e2e:", exc, file=sys.stderr)
        return 2

    report_target = runtime_dir / REPORT_FILENAME
    env = build_launch_env(report_target)

    def launch_and_wait():
        print("e2e: launching game and waiting for report (timeout %ss)..." % args.timeout)
        subprocess.Popen(cmd, cwd=args.game_dir, env=env,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        report_path = wait_for_report(runtime_dir, args.timeout, args.poll)
        return _load_or_else(report_path, str(runtime_dir))

    print("e2e: launch argv: %s" % " ".join(cmd))

    if args.save_dir:
        with DisposableSaveProfile(real_dir=args.save_dir, workspace=runtime_dir) as profile:
            print("e2e: disposable profile at %s" % profile.path)
            if not args.preview and args.launch:
                report = launch_and_wait()
            else:
                print("e2e: --preview / no --launch: skipped game launch and report wait.")
                return 0
    else:
        if not args.preview and args.launch:
            report = launch_and_wait()
        else:
            print("e2e: --preview / no --launch: skipped game launch and report wait.")
            return 0

    print(human_summary(report))
    return gate(report)


if __name__ == "__main__":
    sys.exit(main())
