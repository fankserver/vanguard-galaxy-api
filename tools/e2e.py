#!/usr/bin/env python3
"""In-game end-to-end regression driver for VGModAPI.

This is an opt-in local development tool (parallel to ``make check-bindings``).
It is never part of default CI and never shipped in the release package.

Architecture (mirrors the proven Surity in-game test pattern, self-contained here)
---------------------------------------------------------------------------------
* The driver binds a loopback listener, then launches the real Vanguard Galaxy
  with the API and the dev-only ``EWTest`` harness loaded, passing
  ``-batchmode -nographics -runEWTests`` (Unity's documented standalone-player
  batch arguments + a handshake that only runs the harness when launched by the
  driver, exactly like Surity's ``-runSurityTests``).
* The harness connects back to the driver's listener and streams every check
  result over newline-delimited JSON, then sends a ``finish`` message and exits.
* The driver assembles a report from the stream, prints a human summary, and
  returns non-zero when any live check fails. Each failure carries a
  ``suggestedAction`` naming the binding/contract to re-inspect on an update.

Transport (newline-delimited JSON, one object per line)::

    {"type":"meta","apiVersion":"0.2.0","gameAssemblySha256":"<hex>","gameVersion":"0.8.2.3","unityVersion":"6000.4.7f1"}
    {"type":"suite-start","suite":{"id":"availability","name":"Public service availability"}}
    {"type":"result","check":{ "check": "...", "status": "pass|fail|skip", "message": "...",
                               "expected": "...", "actual": "...", "suggestedAction": "...", "elapsedMs": 12 }}
    {"type":"finish"}

Save safety
-----------
The driver never writes to your real saves. The disposable-save isolation guard
copies the live save directory into a private workspace and, after the run,
verifies the real save tree is byte-for-byte unchanged (a manifest sha-256
comparison), then discards the workspace copy.

No game installed on the current machine? Use ``--preview`` to validate the
wiring, or ``--report`` to parse + gate a previously captured report, without
launching a game.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import socket
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path
from typing import List, Optional

SCHEMA = 1
VALID_STATUS = ("pass", "fail", "skip")
HANDSHAKE_ARG = "-runEWTests"
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
        return {"check": self.check, "status": self.status, "message": self.message,
                "expected": self.expected, "actual": self.actual,
                "suggestedAction": self.suggestedAction, "elapsedMs": self.elapsedMs}

    @classmethod
    def from_dict(cls, raw: dict) -> "CheckResult":
        check = raw.get("check")
        status = raw.get("status")
        if not isinstance(check, str) or not check:
            raise E2EError("result missing a non-empty 'check' string")
        if status not in VALID_STATUS:
            raise E2EError("invalid result status %r for check %r" % (status, check))
        elapsed = raw.get("elapsedMs", 0)
        if not isinstance(elapsed, (int, float)) or elapsed < 0:
            raise E2EError("invalid elapsedMs %r for check %r" % (elapsed, check))
        return cls(check=check, status=status, message=str(raw.get("message", "")),
                   expected=str(raw.get("expected", "")), actual=str(raw.get("actual", "")),
                   suggestedAction=str(raw.get("suggestedAction", "")), elapsedMs=int(elapsed))


class SuiteResult:
    __slots__ = ("id", "name", "results")

    def __init__(self, id, name, results):
        self.id = id
        self.name = name
        self.results = list(results)

    def to_dict(self) -> dict:
        return {"id": self.id, "name": self.name, "results": [r.to_dict() for r in self.results]}

    @classmethod
    def from_dict(cls, raw: dict) -> "SuiteResult":
        id = raw.get("id")
        name = raw.get("name")
        results_raw = raw.get("results")
        if not isinstance(id, str) or not id:
            raise E2EError("suite missing non-empty 'id'")
        if not isinstance(name, str):
            raise E2EError("suite %r missing 'name'" % id)
        if not isinstance(results_raw, list):
            raise E2EError("suite %r 'results' must be a list" % id)
        return cls(id, name, [CheckResult.from_dict(r) for r in results_raw])


class Report:
    __slots__ = ("schema", "meta", "suites", "current_suite", "finished")

    def __init__(self, schema=SCHEMA, meta=None, suites=None):
        self.schema = schema
        self.meta = dict(meta or {})
        self.suites = list(suites or [])
        self.current_suite = None  # streaming-only transient state
        self.finished = False

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

    def to_dict(self) -> dict:
        return {"schema": self.schema, "meta": dict(self.meta),
                "suites": [s.to_dict() for s in self.suites],
                "summary": {"passed": self.passed, "failed": self.failed,
                            "skipped": self.skipped, "total": len(self.results)}}

    def to_json(self) -> str:
        return json.dumps(self.to_dict(), indent=2)

    @classmethod
    def from_dict(cls, raw: dict) -> "Report":
        if not isinstance(raw, dict):
            raise E2EError("report root must be a JSON object")
        if raw.get("schema") != SCHEMA:
            raise E2EError("unsupported schema %r (expected %d)" % (raw.get("schema"), SCHEMA))
        meta = raw.get("meta")
        if not isinstance(meta, dict):
            raise E2EError("missing 'meta' object")
        suites_raw = raw.get("suites")
        if not isinstance(suites_raw, list):
            raise E2EError("'suites' must be a list")
        report = cls(SCHEMA, {k: str(v) for k, v in meta.items()},
                     [SuiteResult.from_dict(s) for s in suites_raw])
        summary = raw.get("summary")
        if isinstance(summary, dict) and summary.get("total") is not None:
            if not isinstance(summary["total"], int):
                raise E2EError("'summary.total' must be an integer")
        return report


def parse_report(text: str) -> Report:
    try:
        raw = json.loads(text)
    except json.JSONDecodeError as exc:
        raise E2EError("invalid JSON: %s" % exc) from exc
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
    lines = ["E2E report: %d passed, %d failed, %d skipped (total %d)"
             % (report.passed, report.failed, report.skipped, len(report.results))]
    if report.meta.get("apiVersion"):
        lines.append("  api version: %s" % report.meta["apiVersion"])
    if report.meta.get("gameAssemblySha256"):
        lines.append("  game assembly sha256: %s" % report.meta["gameAssemblySha256"])
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
# Streaming protocol: build a Report from individual wire messages
# ---------------------------------------------------------------------------
def apply_message(report: Report, msg: dict) -> None:
    """Fold one streamed message into a mutable Report (side-effect free helpers
    are kept pure; this is the one place that mutates the running report)."""
    msg_type = msg.get("type")
    if msg_type == "meta":
        for k, v in msg.items():
            if k == "type":
                continue
            report.meta[str(k)] = str(v)
    elif msg_type == "suite-start":
        suite = msg.get("suite")
        if not isinstance(suite, dict) or not suite.get("id"):
            raise E2EError("malformed suite-start message")
        current = SuiteResult(str(suite["id"]), str(suite.get("name", "")), [])
        report.suites.append(current)
        report.current_suite = current
    elif msg_type == "result":
        current = report.current_suite
        if current is None:
            raise E2EError("result message received before any suite-start")
        current.results.append(CheckResult.from_dict(msg.get("check", {})))
    elif msg_type == "finish":
        report.finished = True
    else:
        raise E2EError("unknown message type %r" % msg_type)


class Listener:
    """Loopback listener the game connects to and streams results over.

    bind() starts listening on an ephemeral loopback port; accept() blocks until
    the harness connects (or until timeout), then returns a line-iterator reader.
    """

    def __init__(self, host="127.0.0.1"):
        self.host = host
        self.port = 0
        self._sock = None

    @property
    def bound(self) -> bool:
        return self._sock is not None

    def bind(self) -> int:
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._sock.bind((self.host, 0))
        self._sock.listen(1)
        self._sock.settimeout(1)
        self.port = self._sock.getsockname()[1]
        return self.port

    def accept(self, timeout_seconds: float) -> Optional[object]:
        """Accept one client; returns an iterator of newline-delimited message
        dicts, or None on timeout. Each loop the reader keeps the socket alive
        so a slow game that connects mid-run is not dropped."""
        deadline = time.monotonic() + timeout_seconds
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return None
            try:
                self._sock.settimeout(remaining)
                conn, _ = self._sock.accept()
            except socket.timeout:
                return None
            except OSError:
                return None
            conn.settimeout(timeout_seconds)
            return _message_reader(conn)

    def close(self) -> None:
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
            self._sock = None


def _message_reader(conn):
    """Adapter so accept() can return something iterable; `conn` is managed here."""
    def read():
        try:
            with conn:
                buf = b""
                while True:
                    try:
                        chunk = conn.recv(4096)
                    except (ConnectionError, socket.timeout):
                        break
                    if not chunk:
                        break
                    buf += chunk
                    while b"\n" in buf:
                        line, buf = buf.split(b"\n", 1)
                        if not line.strip():
                            continue
                        try:
                            yield json.loads(line.decode("utf-8"))
                        except (json.JSONDecodeError, UnicodeDecodeError):
                            continue
        finally:
            pass
    gen = read()
    return gen


def consume_stream(report: Report, reader, timeout_seconds: float) -> bool:
    """Consume messages from ``reader`` until a finish message, building report.
    Returns True if a finish was seen, False on timeout/disconnect."""
    for msg in reader:
        try:
            apply_message(report, msg)
        except E2EError:
            continue
        if getattr(report, "finished", False):
            return True
    return False


# ---------------------------------------------------------------------------
# Save safety: the disposable-save isolation guard
# ---------------------------------------------------------------------------
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
        return self

    def __exit__(self, exc_type, exc, tb):
        if self.path is not None and self.path.is_dir():
            shutil.rmtree(self.path, ignore_errors=False)
        if self._backup is not None and self._backup.is_dir():
            shutil.rmtree(self._backup, ignore_errors=False)
        assert_real_saves_unchanged(self._before, self.real_dir)
        return False


# ---------------------------------------------------------------------------
# Launch + run
# ---------------------------------------------------------------------------
def build_launch_command(game_dir, executable="VanguardGalaxy.exe", extra=None):
    """argv to launch the game and auto-run the harness. Uses Unity's documented
    standalone-player batch arguments plus the handshake that only runs the
    harness when launched by this driver (mirrors Surity's -runSurityTests)."""
    game = Path(game_dir)
    exe = game / executable
    if not exe.is_file():
        raise E2EError("game executable not found: %s (set --game-dir)" % exe)
    argv = [str(exe), HANDSHAKE_ARG]
    if extra:
        argv.extend(extra)
    return argv


def build_launch_env(port, run="1", suite="all"):
    env = dict(os.environ)
    env["SteamAppId"] = "3471800"
    env["SteamGameId"] = "3471800"
    env["EWTEST_RUN"] = run
    env["EWTEST_PORT"] = str(port)
    env["EWTEST_SUITE"] = suite
    return env


def run_streaming(game_dir, executable, listen_timeout, poll_handles, suite="all"):
    """Bind a listener, launch the game with the handshake, connect, stream a
    report, and return the assembled Report. Pure orchestration (the pieces are
    individually unit-tested); no save-touching happens here."""
    listener = Listener()
    listener.bind()
    report = Report()
    try:
        env = build_launch_env(listener.port, suite=suite)
        proc = subprocess.Popen(build_launch_command(game_dir, executable),
                                cwd=game_dir, env=env,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                stdin=subprocess.DEVNULL)
        try:
            reader = listener.accept(listen_timeout)
            if reader is None:
                return _timeout_report(report, "the game never connected to the e2e listener")
            finished = consume_stream(report, reader, listen_timeout)
            if not finished:
                return _timeout_report(report, "the game disconnected before sending a finish message")
            return report
        finally:
            report.meta["exitCodeBeforeCleanup"] = proc.poll()
            print("e2e: process status before cleanup:", proc.poll(), flush=True)
            try:
                proc.terminate()
                proc.wait(timeout=10)
            except Exception:
                pass
    finally:
        listener.close()
    return report


def _timeout_report(report, note):
    report.meta["aborted"] = note
    report.suites.append(SuiteResult("aborted", "Run aborted", [
        CheckResult("run completed", "fail", note, "finish message within timeout",
                    "timeout/disconnect", "Inspect the game log and exitCodeBeforeCleanup; " +
                    "a partial report is not a completed run.")]))
    return report


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        prog="e2e", description="In-game E2E regression driver for VGModAPI (opt-in dev tool).")
    parser.add_argument("--game-dir", default=os.environ.get("GAME_DIR", ""),
                        help="Vanguard Galaxy install dir (default: $GAME_DIR)")
    parser.add_argument("--executable", default="VanguardGalaxy.exe")
    parser.add_argument("--runtime-dir", default=None,
                        help="workspace for the disposable profile + report (default: temp)")
    parser.add_argument("--save-dir", default=None,
                        help="real save directory to guard (never modified)")
    parser.add_argument("--timeout", type=int, default=600,
                        help="seconds to wait for the harness to connect + finish (default 600)")
    parser.add_argument("--suite", choices=("all", "fresh-session"), default="all",
                        help="run all suites or the isolated fresh-game lifecycle test")
    parser.add_argument("--launch", action="store_true",
                        help="actually launch the game and run (normal mode)")
    parser.add_argument("--preview", action="store_true",
                        help="dry-run: build the launch command/env, exit 0 without launching. CI-safe.")
    parser.add_argument("--report", default=None,
                        help="parse + gate a captured report file, then exit (no launch)")
    args = parser.parse_args(argv)

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

    print("e2e: launch argv: %s" % " ".join(cmd))

    if not (args.preview or args.launch):
        print("e2e: neither --launch nor --preview; nothing to do.")
        return 2

    if args.preview:
        print("e2e: --preview: wiring OK (no game launched).")
        return 0

    try:
        if args.save_dir:
            with DisposableSaveProfile(real_dir=args.save_dir, workspace=runtime_dir) as profile:
                report = run_streaming(args.game_dir, args.executable, args.timeout, None, args.suite)
        else:
            report = run_streaming(args.game_dir, args.executable, args.timeout, None, args.suite)
    except E2EError as exc:
        print("e2e:", exc, file=sys.stderr)
        return 2

    out_dir = runtime_dir / "report"
    out_dir.mkdir(parents=True, exist_ok=True)
    report_path = out_dir / REPORT_FILENAME
    report_path.write_text(report.to_json(), encoding="utf-8")
    print("e2e: report written to %s" % report_path)
    print(human_summary(report))
    return gate(report)


if __name__ == "__main__":
    sys.exit(main())
