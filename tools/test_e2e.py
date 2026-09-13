import json
import socket
import tempfile
import threading
import time
import unittest
from pathlib import Path

from e2e import (
    CheckResult, E2EError, SaveGuardError, SuiteResult, DisposableSaveProfile,
    HANDSHAKE_ARG, Listener, REPORT_FILENAME, Report,
    apply_message, assert_real_saves_unchanged, build_launch_command,
    build_launch_env, consume_stream, gate, human_summary, load_report,
    parse_report, run_streaming, tree_manifest, _timeout_report,
)


def sample_report():
    return {
        "schema": 1,
        "meta": {"apiVersion": "0.2.0", "gameAssemblySha256": "a2aad60b", "gameVersion": "0.8.2.3"},
        "suites": [{
            "id": "availability", "name": "Public service availability",
            "results": [
                {"check": "SessionTracking available", "status": "pass", "message": "ok",
                 "expected": "available", "actual": "available", "suggestedAction": "", "elapsedMs": 1},
                {"check": "World authoring available", "status": "fail", "message": "hook did not bind",
                 "expected": "available", "actual": "BindingFailed: SessionTracking",
                 "suggestedAction": "re-inspect SessionTracking in the updated Assembly-CSharp.dll and update BindingCatalog",
                 "elapsedMs": 5},
            ],
        }],
        "summary": {"passed": 1, "failed": 1, "skipped": 0, "total": 2},
    }


def wire_frames(*messages):
    return "\n".join(json.dumps(m) for m in messages) + "\n"


META = {"type": "meta", "apiVersion": "0.2.0", "gameAssemblySha256": "a2aad60b"}
SUITE = {"type": "suite-start", "suite": {"id": "availability", "name": "Public service availability"}}
RESULT_PASS = {"type": "result", "check": {"check": "SessionTracking available", "status": "pass",
                                           "message": "ok", "expected": "available", "actual": "available",
                                           "suggestedAction": "", "elapsedMs": 1}}
RESULT_FAIL = {"type": "result", "check": {"check": "World available", "status": "fail", "message": "no",
                                           "expected": "available", "actual": "BindingFailed",
                                           "suggestedAction": "re-inspect binding", "elapsedMs": 4}}
FINISH = {"type": "finish"}


class ReportParseTests(unittest.TestCase):
    def test_parse_valid_report(self):
        report = parse_report(json.dumps(sample_report()))
        self.assertEqual((report.passed, report.failed, report.skipped), (1, 1, 0))
        self.assertEqual(report.failures[0].check, "World authoring available")
        self.assertIn("BindingCatalog", report.failures[0].suggestedAction)

    def test_schema_mismatch_rejected(self):
        raw = sample_report(); raw["schema"] = 2
        with self.assertRaises(E2EError):
            parse_report(json.dumps(raw))

    def test_bad_status_rejected(self):
        raw = sample_report(); raw["suites"][0]["results"][0]["status"] = "warn"
        with self.assertRaises(E2EError):
            parse_report(json.dumps(raw))

    def test_lying_summary_cannot_mask_failure(self):
        raw = sample_report(); raw["summary"] = {"passed": 2, "failed": 0, "skipped": 0, "total": 2}
        self.assertEqual(gate(parse_report(json.dumps(raw))), 1)

    def test_load_report_file(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / REPORT_FILENAME
            p.write_text(json.dumps(sample_report()))
            self.assertEqual(load_report(p).failed, 1)
        with self.assertRaises(E2EError):
            load_report(Path(td) / "missing.json")


class GateAndSummaryTests(unittest.TestCase):
    def test_gate_single(self):
        raw = sample_report()
        raw["suites"][0]["results"] = [raw["suites"][0]["results"][0]]
        self.assertEqual(gate(parse_report(json.dumps(raw))), 0)
        self.assertEqual(gate(parse_report(json.dumps(sample_report()))), 1)

    def test_human_summary_lists_failure_and_fix(self):
        text = human_summary(parse_report(json.dumps(sample_report())))
        self.assertIn("1 failed", text)
        self.assertIn("World authoring available", text)
        self.assertIn("BindingCatalog", text)


class StreamingProtocolTests(unittest.TestCase):
    def test_apply_message_builds_report(self):
        report = Report()
        apply_message(report, META)
        apply_message(report, SUITE)
        apply_message(report, RESULT_PASS)
        apply_message(report, RESULT_FAIL)
        self.assertEqual(report.meta["gameAssemblySha256"], "a2aad60b")
        self.assertEqual((report.passed, report.failed), (1, 1))
        self.assertEqual(report.failures[0].suggestedAction, "re-inspect binding")

    def test_apply_result_before_suite_raises(self):
        report = Report()
        with self.assertRaises(E2EError):
            apply_message(report, RESULT_PASS)

    def test_apply_unknown_type_raises(self):
        report = Report()
        with self.assertRaises(E2EError):
            apply_message(report, {"type": "bogus"})

    def test_listener_streams_to_report(self):
        listener = Listener()
        port = listener.bind()

        def game():
            time.sleep(0.05)
            with socket.create_connection(("127.0.0.1", port), timeout=5) as s:
                s.sendall(wire_frames(META, SUITE, RESULT_PASS, RESULT_FAIL, FINISH).encode())

        t = threading.Thread(target=game)
        t.start()
        reader = listener.accept(5)
        self.assertIsNotNone(reader)
        report = Report()
        finished = consume_stream(report, reader, 5)
        listener.close()
        t.join(timeout=5)
        self.assertTrue(finished)
        self.assertEqual((report.passed, report.failed), (1, 1))

    def test_listener_accept_timeout_returns_none(self):
        listener = Listener(); listener.bind()
        self.assertIsNone(listener.accept(0.1))
        listener.close()


class IsolationTests(unittest.TestCase):
    def make_real(self, base):
        real = base / "real-saves"
        real.mkdir()
        (real / "slot1.json").write_text("{\"progress\": 7}")
        (real / "meta.txt").write_text("checksum=x")
        return real

    def test_tree_manifest_stable_and_rejects_change(self):
        with tempfile.TemporaryDirectory() as td:
            real = self.make_real(Path(td))
            self.assertEqual(len(tree_manifest(real)), 2)
            original = tree_manifest(real)
            assert_real_saves_unchanged(original, real)
            (real / "slot1.json").write_text("tampered")
            with self.assertRaises(SaveGuardError):
                assert_real_saves_unchanged(original, real)

    def test_disposable_profile_keeps_real_saves_untouched(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td); real = self.make_real(base)
            before = tree_manifest(real)
            with DisposableSaveProfile(real_dir=real, workspace=base / "work") as profile:
                (profile.path / "state.bin").write_bytes(b"\x01")
            self.assertEqual(tree_manifest(real), before)
            self.assertFalse(profile.path.exists())
            self.assertEqual(list(Path(base / "work").glob("vgmodapi-e2e-*")), [])

    def test_disposable_profile_detects_real_save_write(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td); real = self.make_real(base)
            with self.assertRaises(SaveGuardError):
                with DisposableSaveProfile(real_dir=real, workspace=base / "work"):
                    (real / "slot1.json").write_text("overwritten")


class LaunchTests(unittest.TestCase):
    def test_build_launch_command_normal_player_and_handshake(self):
        with tempfile.TemporaryDirectory() as td:
            game = Path(td) / "game"; game.mkdir()
            (game / "VanguardGalaxy.exe").write_bytes(b"MZ")
            cmd = build_launch_command(str(game))
            self.assertEqual(cmd[0], str(game / "VanguardGalaxy.exe"))
            self.assertNotIn("-batchmode", cmd)
            self.assertNotIn("-nographics", cmd)
            self.assertIn(HANDSHAKE_ARG, cmd)

    def test_build_launch_command_missing_exe(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(E2EError):
                build_launch_command(str(Path(td) / "no-game"))

    def test_build_launch_env_sets_port_contract(self):
        env = build_launch_env(54321)
        self.assertEqual(env["EWTEST_RUN"], "1")
        self.assertEqual(env["EWTEST_PORT"], "54321")
        self.assertEqual(env["SteamAppId"], "3471800")
        self.assertEqual(env["SteamGameId"], "3471800")
        self.assertEqual(env["EWTEST_SUITE"], "all")

    def test_select_fresh_session_case(self):
        self.assertEqual(build_launch_env(54321, suite="fresh-session")["EWTEST_SUITE"], "fresh-session")

    def test_disconnect_after_passing_check_still_fails(self):
        report = Report()
        for message in (META, SUITE, RESULT_PASS):
            apply_message(report, message)
        _timeout_report(report, "disconnected without finish")
        self.assertEqual(report.passed, 1)
        self.assertEqual(report.failed, 1)
        self.assertEqual(gate(report), 1)


class CliTests(unittest.TestCase):
    def test_report_mode_gates(self):
        import e2e as module
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / REPORT_FILENAME
            p.write_text(json.dumps(sample_report()))
            self.assertEqual(module.main(["--report", str(p)]), 1)
            raw = sample_report(); raw["suites"][0]["results"] = [raw["suites"][0]["results"][0]]
            p.write_text(json.dumps(raw))
            self.assertEqual(module.main(["--report", str(p)]), 0)

    def test_preview_mode_is_ci_safe(self):
        import e2e as module
        with tempfile.TemporaryDirectory() as td:
            game = Path(td) / "game"; game.mkdir()
            (game / "VanguardGalaxy.exe").write_bytes(b"MZ")
            self.assertEqual(module.main(["--game-dir", str(game), "--preview"]), 0)


if __name__ == "__main__":
    unittest.main()
