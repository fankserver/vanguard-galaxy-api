import json
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import unittest
from unittest.mock import Mock, patch

import e2e


RESULT = dict(type="result", id="fresh-session", status="pass", detail="ok", binding="", elapsedMs=10)
META = dict(type="meta", runId="run", gameVersion="test")
FINISH = dict(type="finish")


class ProtocolTests(unittest.TestCase):
    def stream(self, messages):
        reader, writer = socket.socketpair()
        self.addCleanup(reader.close)
        self.addCleanup(writer.close)
        payload = b"".join(json.dumps(m).encode() + b"\n" for m in messages)
        writer.sendall(payload)
        writer.shutdown(socket.SHUT_WR)
        return reader

    def test_complete_test_passes(self):
        report = e2e.new_report()
        e2e.consume(self.stream([META, RESULT, FINISH]), report, "run", time.monotonic() + 2)
        self.assertEqual(e2e.gate(report), 0)

    def test_disconnect_after_pass_cannot_pass(self):
        report = e2e.new_report()
        with self.assertRaisesRegex(e2e.E2EError, "disconnected"):
            e2e.consume(self.stream([META, RESULT]), report, "run", time.monotonic() + 2)
        self.assertEqual(e2e.gate(report), 1)

    def test_no_results_cannot_pass(self):
        report = e2e.new_report()
        report["finished"] = True
        self.assertEqual(e2e.gate(report), 1)
        with self.assertRaises(e2e.E2EError):
            e2e.consume(self.stream([META, FINISH]), e2e.new_report(), "run", time.monotonic() + 2)

    def test_invalid_order_duplicate_and_unknown_result_fail(self):
        for messages in ([RESULT, FINISH], [META, META], [META, RESULT, RESULT],
                         [META, dict(RESULT, id="unrequested")], [META, dict(RESULT, status="skip")],
                         [META, dict(RESULT, elapsedMs=-1)], [META, []]):
            with self.subTest(messages=messages), self.assertRaises(e2e.E2EError):
                e2e.consume(self.stream(messages), e2e.new_report(), "run", time.monotonic() + 2)

    def test_wrong_run_rejected(self):
        with self.assertRaisesRegex(e2e.E2EError, "run ID"):
            e2e.consume(self.stream([META]), e2e.new_report(), "other", time.monotonic() + 2)

    def test_malformed_json_not_silently_ignored(self):
        conn = Mock()
        conn.recv.return_value = b"not json\n"
        with self.assertRaisesRegex(e2e.E2EError, "Malformed"):
            e2e.consume(conn, e2e.new_report(), "run", time.monotonic() + 2)

    def test_total_deadline_even_when_stream_continues(self):
        with self.assertRaisesRegex(e2e.E2EError, "deadline"):
            e2e.consume(Mock(), e2e.new_report(), "run", time.monotonic() - 1)

    def test_report_round_trip_keeps_process_state_types(self):
        report = e2e.new_report()
        report.update(finished=True, results=[{k: v for k, v in RESULT.items() if k != "type"}])
        report["meta"]["exitCodeBeforeCleanup"] = None
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "report.json"
            path.write_text(json.dumps(report))
            loaded = e2e.read_report(path)
            self.assertIsNone(loaded["meta"]["exitCodeBeforeCleanup"])
            self.assertEqual(e2e.gate(loaded), 0)


class SafetyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.game = self.root / "game"
        self.build = self.root / "build"
        self.build.mkdir()
        for name in e2e.ASSEMBLIES:
            (self.build / name).write_bytes(b"test assembly")
        self.bep = self.game / "BepInEx"
        for name in ("core", "plugins", "config"):
            (self.bep / name).mkdir(parents=True)
        (self.bep / "core/BepInEx.dll").write_bytes(b"core")
        (self.bep / "plugins/user.dll").write_bytes(b"user mod")
        (self.bep / "config/user.cfg").write_text("user config")

    def assert_restored(self):
        self.assertEqual((self.bep / "plugins/user.dll").read_bytes(), b"user mod")
        self.assertEqual((self.bep / "config/user.cfg").read_text(), "user config")
        self.assertFalse((self.bep / ".vgmodapi-e2e-backup").exists())
        self.assertFalse((self.bep / "plugins/VGModAPI.E2E").exists())

    def test_stage_only_built_assemblies_and_restore(self):
        with e2e.GameInstallation(self.game, self.build):
            self.assertFalse((self.bep / "plugins/user.dll").exists())
            self.assertEqual(len(list((self.bep / "plugins/VGModAPI.E2E").iterdir())), len(e2e.ASSEMBLIES))
            (self.bep / "config/vgmodapi.cfg").write_text("generated")
        self.assert_restored()

    def test_restore_after_test_exception(self):
        with self.assertRaises(RuntimeError):
            with e2e.GameInstallation(self.game, self.build):
                raise RuntimeError("test failed")
        self.assert_restored()

    def test_partial_staging_failure_restores(self):
        with patch("e2e.shutil.copy2", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                with e2e.GameInstallation(self.game, self.build):
                    self.fail("should not reach test")
        self.assert_restored()

    def test_interrupted_run_backup_blocks_retry(self):
        with self.assertRaises(e2e.CleanupError):
            with e2e.GameInstallation(self.game, self.build):
                raise e2e.CleanupError("still running")
        self.assertTrue((self.bep / ".vgmodapi-e2e-backup/plugins/user.dll").exists())
        with self.assertRaisesRegex(e2e.E2EError, "backup exists"):
            with e2e.GameInstallation(self.game, self.build):
                self.fail("must not overwrite backup")

    def test_missing_build_does_not_touch_installation(self):
        (self.build / e2e.ASSEMBLIES[0]).unlink()
        with self.assertRaises(e2e.E2EError):
            with e2e.GameInstallation(self.game, self.build):
                self.fail()
        self.assert_restored()

    def test_save_guard_is_read_only_and_detects_changes(self):
        saves = self.root / "Saves"
        saves.mkdir()
        slot = saves / "slot"
        slot.write_bytes(b"original")
        with e2e.SaveGuard(saves):
            self.assertEqual(slot.read_bytes(), b"original")
        with self.assertRaisesRegex(e2e.E2EError, "changed"):
            with e2e.SaveGuard(saves):
                slot.write_bytes(b"changed")
        self.assertEqual(slot.read_bytes(), b"changed")  # No fabricated isolation/rollback.


class ProcessTests(unittest.TestCase):
    def test_steam_context_and_explicit_handshake(self):
        with patch("e2e.time.time", return_value=1000):
            env = e2e.launch_env(1234, "unique", 80)
        self.assertEqual(env["SteamAppId"], "3471800")
        self.assertEqual(env["SteamGameId"], "3471800")
        self.assertEqual(env["VGMODAPI_E2E_RUN"], "unique")
        self.assertEqual(env["VGMODAPI_E2E_DEADLINE"], "1080000")

    def test_existing_process_is_refused_not_killed(self):
        with patch("e2e.subprocess.check_output", return_value='"VanguardGalaxy.exe","1234"\n'):
            with self.assertRaises(e2e.E2EError):
                e2e.ensure_game_stopped()

    def test_natural_exit_is_not_controller_termination(self):
        proc = Mock(returncode=-1)
        proc.poll.return_value = None
        report = e2e.new_report()
        e2e.stop_owned_process(proc, report)
        proc.terminate.assert_not_called()
        self.assertIsNone(report["meta"]["exitCodeBeforeCleanup"])
        self.assertFalse(report["meta"]["terminatedByController"])

    def test_timeout_terminates_only_owned_process(self):
        proc = Mock(returncode=1)
        proc.poll.return_value = None
        proc.wait.side_effect = [subprocess.TimeoutExpired("game", 5), 1]
        report = e2e.new_report()
        e2e.stop_owned_process(proc, report)
        proc.terminate.assert_called_once()
        self.assertTrue(report["meta"]["terminatedByController"])

    def test_unstoppable_process_retains_staging(self):
        proc = Mock()
        proc.wait.side_effect = subprocess.TimeoutExpired("game", 5)
        with self.assertRaises(e2e.CleanupError):
            e2e.stop_owned_process(proc, e2e.new_report())


if __name__ == "__main__":
    unittest.main()
