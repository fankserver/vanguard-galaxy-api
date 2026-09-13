#!/usr/bin/env python3
"""Opt-in Windows game tests. Builds are staged by make e2e-build; no public CI launch."""

import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import time
import uuid

CASE = "fresh-session"
KNOWN_CASES = ("fresh-session", "pocket-worlds", "story-missions", "observation",
              "cargo-recovery", "station-commerce", "ui-surfaces")
HANDSHAKE = "--vgmodapi-e2e"
ASSEMBLIES = ("VGModAPI.dll", "VGModAPI.Core.dll", "VGModAPI.Abstractions.dll",
              "VGModAPI.Unity.dll", "VGModAPI.E2E.dll", "PocketWorlds.dll", "CargoRecovery.dll",
              "StoryMissions.dll", "StationCommerce.dll", "StationCommerceB.dll", "UiSurfaces.dll",
              "Observation.dll", "Newtonsoft.Json.dll")


class E2EError(Exception):
    pass


class CleanupError(E2EError):
    """The owned process could not be stopped; installed directories must not move."""


def manifest(root):
    root = Path(root)
    if not root.is_dir():
        raise E2EError(f"Save directory does not exist: {root}")
    result = {}
    for path in sorted(root.rglob("*")):
        if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
            raise E2EError(f"Save guard does not support linked paths: {path}")
        if path.is_file():
            result[path.relative_to(root).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


class SaveGuard:
    """Detects writes; not a sandbox, disposable profile, or rollback mechanism."""
    def __init__(self, root, allow_missing=False):
        self.root = Path(root)
        self.allow_missing = allow_missing

    def snapshot(self):
        if self.allow_missing and not self.root.exists() and not self.root.is_symlink():
            return None  # Preserve absence, not merely an empty file manifest.
        return manifest(self.root)

    def __enter__(self):
        self.before = self.snapshot()
        return self

    def __exit__(self, *exc):
        if self.snapshot() != self.before:
            raise E2EError("Real save files changed during E2E; inspect the game log. No automatic rollback attempted.")


class GameInstallation:
    """Reversible plugins/config staging. An interrupted run leaves an explicit backup."""
    def __init__(self, game, build):
        self.root = Path(game) / "BepInEx"
        self.build = Path(build)
        self.backup = self.root / ".vgmodapi-e2e-backup"
        self.moved = []
        self.created = []

    def __enter__(self):
        if self.backup.exists():
            raise E2EError(f"Previous staging backup exists: {self.backup}. Inspect/recover it before retrying.")
        if not (self.root / "core" / "BepInEx.dll").is_file():
            raise E2EError("BepInEx 5 installation not found.")
        for name in ASSEMBLIES:
            if not (self.build / name).is_file():
                raise E2EError(f"Missing {name}; run make e2e-build.")
        for name in ("plugins", "config"):
            path = self.root / name
            if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
                raise E2EError(f"Refusing to replace linked directory: {path}")
        self.backup.mkdir()
        try:
            for name in ("plugins", "config"):
                path = self.root / name
                if path.exists():
                    path.rename(self.backup / name)
                    self.moved.append(name)
                path.mkdir()
                self.created.append(name)
            target = self.root / "plugins" / "VGModAPI.E2E"
            target.mkdir()
            for name in ASSEMBLIES:
                shutil.copy2(self.build / name, target / name)
            # The staging vacuums the real config/ for isolation, which drops the API back to its
            # disabled-by-default providers. Enable the ones the example cases exercise (Story drives
            # station-commerce's errand and the story-missions case; Bars backs the bar-contact examples).
            # This mirrors what a normal install has, not a product-default change.
            (self.root / "config" / "vgmodapi.cfg").write_text(
                "# E2E test config: enable the providers the example cases exercise.\n"
                "[Story]\nEnabled = true\n"
                "[Bars]\nEnabled = true\n",
                encoding="utf-8")
        except BaseException:
            self.restore()
            raise
        return self

    def restore(self):
        for name in reversed(self.created):
            shutil.rmtree(self.root / name)
        for name in reversed(self.moved):
            (self.backup / name).rename(self.root / name)
        self.backup.rmdir()

    def __exit__(self, kind, value, traceback):
        if kind is not None and issubclass(kind, CleanupError):
            return False  # Never move DLLs beneath a process we failed to stop.
        self.restore()


def failure(report, detail, binding="controller"):
    report["results"].append(dict(id=CASE, status="fail", detail=detail, binding=binding, elapsedMs=0))


def new_report():
    return dict(schema=2, meta={}, results=[], finished=False)


def gate(report):
    results = report.get("results", [])
    return 0 if (report.get("finished") is True and results
                 and all(r.get("status") == "pass" for r in results)) else 1


def read_report(path):
    try:
        report = json.loads(Path(path).read_text(encoding="utf-8"))
        if not isinstance(report, dict) or report.get("schema") != 2:
            raise ValueError("unsupported report schema")
        if not isinstance(report.get("results"), list) or not isinstance(report.get("meta"), dict):
            raise ValueError("missing metadata/results")
        for result in report["results"]:
            validate_result(result)
        return report
    except (OSError, ValueError, TypeError, AttributeError) as error:
        raise E2EError(f"Invalid E2E report: {error}") from error


def validate_result(result):
    if not isinstance(result, dict) or result.get("id") not in KNOWN_CASES or result.get("status") not in ("pass", "fail"):
        raise E2EError("Invalid test result or unexpected test ID.")
    if not isinstance(result.get("detail"), str) or not isinstance(result.get("binding"), str):
        raise E2EError("Result requires detail and binding strings.")
    elapsed = result.get("elapsedMs")
    if type(elapsed) is not int or elapsed < 0:
        raise E2EError("Invalid result elapsedMs.")


def consume(conn, report, run_id, deadline):
    buffer = b""
    while not report["finished"]:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise E2EError("Run deadline expired before finish.")
        conn.settimeout(remaining)
        chunk = conn.recv(4096)
        if not chunk:
            raise E2EError("Game disconnected before finish.")
        buffer += chunk
        if len(buffer) > 65536:
            raise E2EError("Oversized protocol message.")
        while b"\n" in buffer:
            line, buffer = buffer.split(b"\n", 1)
            try:
                msg = json.loads(line.decode("utf-8"))
            except (ValueError, UnicodeError) as error:
                raise E2EError("Malformed protocol message.") from error
            if not isinstance(msg, dict):
                raise E2EError("Protocol message must be an object.")
            kind = msg.get("type")
            if kind == "meta" and not report["meta"]:
                if msg.get("runId") != run_id:
                    raise E2EError("Unexpected controller run ID.")
                report["meta"] = {k: v for k, v in msg.items() if k != "type"}
            elif kind == "result" and report["meta"] and not report["results"]:
                if msg.get("id") != CASE:
                    raise E2EError(f"Result for unrequested case: {msg.get('id')!r} (requested {CASE!r}).")
                validate_result(msg)
                report["results"].append({k: v for k, v in msg.items() if k != "type"})
            elif kind == "finish" and report["results"]:
                report["finished"] = True
                return
            else:
                raise E2EError(f"Unexpected protocol message/order: {kind!r}")


def launch_env(port, run_id, timeout, screenshots=None):
    env = dict(os.environ)
    env.update(SteamAppId="3471800", SteamGameId="3471800",
               VGMODAPI_E2E_PORT=str(port), VGMODAPI_E2E_RUN=run_id,
               VGMODAPI_E2E_CASE=CASE, VGMODAPI_E2E_DEADLINE=str(int((time.time() + timeout) * 1000)))
    if screenshots is not None:
        env["VGMODAPI_E2E_SCREENSHOTS"] = str(screenshots)
    return env


def stop_owned_process(proc, report):
    report["meta"]["exitCodeBeforeCleanup"] = proc.poll()
    try:
        proc.wait(timeout=5)
        report["meta"]["terminatedByController"] = False
    except subprocess.TimeoutExpired:
        proc.terminate()
        report["meta"]["terminatedByController"] = True
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired as error:
                raise CleanupError("Owned game could not be stopped; staging backup retained.") from error
    report["meta"]["exitCode"] = proc.returncode


def run_game(game, runtime, timeout, report):
    run_id = uuid.uuid4().hex
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        deadline = time.monotonic() + timeout
        listener.settimeout(timeout)
        command = [str(game / "VanguardGalaxy.exe"), HANDSHAKE, "-logFile", str(runtime / "player.log")]
        proc = subprocess.Popen(command, cwd=game,
                                env=launch_env(listener.getsockname()[1], run_id, timeout - 10, runtime / "screenshots"),
                                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            conn, _ = listener.accept()
            with conn:
                consume(conn, report, run_id, deadline)
        except (E2EError, OSError) as error:
            failure(report, str(error))
        finally:
            try:
                stop_owned_process(proc, report)
            except BaseException as error:
                raise CleanupError("Cannot verify game shutdown; staging backup retained.") from error


def ensure_game_stopped():
    output = subprocess.check_output(["tasklist.exe", "/FO", "CSV", "/NH"], text=True)
    if any(row and row[0].lower() == "vanguardgalaxy.exe" for row in csv.reader(output.splitlines())):
        raise E2EError("VanguardGalaxy.exe is already running. Close it before E2E; existing processes are never killed.")


def summary(report):
    passed = sum(r["status"] == "pass" for r in report["results"])
    failed = sum(r["status"] == "fail" for r in report["results"])
    print(f"E2E: {passed} passed, {failed} failed; finished={report['finished']}")
    for result in report["results"]:
        print(f"  {result['status']}: {result['id']}: {result['detail']}")
        if result["binding"]:
            print(f"    inspect: {result['binding']}")


def nonempty_path(value):
    if not value.strip():
        raise argparse.ArgumentTypeError("Path must not be empty.")
    return Path(value)


def main(argv=None):
    global CASE
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=nonempty_path)
    parser.add_argument("--build-dir", type=nonempty_path, default=Path("artifacts/e2e/plugin"))
    parser.add_argument("--runtime-dir", type=nonempty_path, default=Path("artifacts/e2e/run"))
    parser.add_argument("--save-dir", type=nonempty_path)
    parser.add_argument("--timeout", type=int, default=90)
    parser.add_argument("--case", choices=KNOWN_CASES, default=CASE)
    parser.add_argument("--launch", action="store_true", help="explicit permission to stage plugins and launch the game")
    parser.add_argument("--report", type=nonempty_path, help="read/gate a previous report without a game")
    args = parser.parse_args(argv)
    if args.report:
        report = read_report(args.report)
        summary(report)
        return gate(report)
    if not args.launch or args.game_dir is None:
        parser.error("--launch and --game-dir are required")
    if args.timeout < 20:
        parser.error("--timeout must be at least 20 seconds (reserves 10 seconds for the in-game failure report)")
    CASE = args.case
    if os.name != "nt":
        raise E2EError("Run the controller with Windows Python (py.exe under WSL); the game uses Windows loopback.")
    game = args.game_dir.resolve()
    if not (game / "VanguardGalaxy.exe").is_file():
        raise E2EError("Game executable not found.")
    ensure_game_stopped()
    runtime = args.runtime_dir.resolve()
    build = args.build_dir.resolve()
    saves = args.save_dir or Path(os.environ["USERPROFILE"]) / "AppData/LocalLow/Bat Roost Games/VanguardGalaxy/Saves"
    saves = saves.resolve()
    if runtime == build or runtime in build.parents or build in runtime.parents:
        raise E2EError("Build and runtime directories must be separate.")
    if runtime == saves or saves in runtime.parents or runtime in saves.parents:
        raise E2EError("Runtime directory must not overlap real saves.")
    for path in (runtime, build):
        if path == game or game in path.parents or path in game.parents:
            raise E2EError("Build/runtime paths must be outside the game installation.")
    runtime.mkdir(parents=True, exist_ok=True)
    shutil.rmtree(runtime / "screenshots", ignore_errors=True)
    report = new_report()
    try:
        with SaveGuard(saves, allow_missing=args.save_dir is None), GameInstallation(game, build):
            run_game(game, runtime, args.timeout, report)
        report["meta"]["realSavesUnchanged"] = True
        report["meta"]["installationRestored"] = True
    except (E2EError, OSError, KeyboardInterrupt) as error:
        failure(report, f"{type(error).__name__}: {error}")
    finally:
        (runtime / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    summary(report)
    return gate(report)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (E2EError, OSError) as error:
        print(f"e2e: {error}", file=sys.stderr)
        sys.exit(2)
