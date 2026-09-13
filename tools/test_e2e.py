import json
import tempfile
import time
import unittest
from pathlib import Path

from e2e import (
    CheckResult, E2EError, SaveGuardError, SuiteResult, DisposableSaveProfile,
    REPORT_FILENAME, build_launch_command, build_launch_env,
    assert_real_saves_unchanged,
    gate, human_summary, load_report, parse_report, tree_manifest,
    wait_for_report,
)


def sample_report():
    return {
        "schema": 1,
        "meta": {
            "apiVersion": "0.2.0",
            "gameAssemblySha256": "a2aad60bc68c31baccd636587d3c5ba4e651eacda59b0af42cd4f17f864284fb",
            "gameVersion": "0.8.2.3",
            "unityVersion": "6000.4.7f1",
            "startedUtc": "2026-01-01T00:00:00Z",
            "finishedUtc": "2026-01-01T00:00:05Z",
        },
        "suites": [
            {
                "id": "availability",
                "name": "Public service availability",
                "results": [
                    {"check": "SessionTracking available", "status": "pass",
                     "message": "ok", "expected": "available",
                     "actual": "available", "suggestedAction": "", "elapsedMs": 1},
                    {"check": "World authoring available", "status": "fail",
                     "message": "hook did not bind", "expected": "available",
                     "actual": "BindingFailed: SessionTracking",
                     "suggestedAction": "re-inspect SessionTracking in the updated Assembly-CSharp.dll and update BindingCatalog",
                     "elapsedMs": 5},
                ],
            }
        ],
        "summary": {"passed": 1, "failed": 1, "skipped": 0, "total": 2},
    }


class ReportParseTests(unittest.TestCase):
    def test_parse_valid_report(self):
        report = parse_report(json.dumps(sample_report()))
        self.assertEqual(report.passed, 1)
        self.assertEqual(report.failed, 1)
        self.assertEqual(report.skipped, 0)
        self.assertEqual(len(report.failures), 1)
        self.assertEqual(report.failures[0].check, "World authoring available")
        self.assertIn("BindingCatalog", report.failures[0].suggestedAction)

    def test_schema_mismatch_rejected(self):
        raw = sample_report()
        raw["schema"] = 2
        with self.assertRaises(E2EError):
            parse_report(json.dumps(raw))

    def test_bad_status_rejected(self):
        raw = sample_report()
        raw["suites"][0]["results"][0]["status"] = "warn"
        with self.assertRaises(E2EError):
            parse_report(json.dumps(raw))

    def test_non_json_rejected(self):
        with self.assertRaises(E2EError):
            parse_report("not json {")

    def test_lying_summary_cannot_mask_failure(self):
        # Even if the writer's summary claims zero failures, the parser recomputes
        # from the results, so a game-update break is never hidden.
        raw = sample_report()
        raw["summary"] = {"passed": 2, "failed": 0, "skipped": 0, "total": 2}
        report = parse_report(json.dumps(raw))
        self.assertEqual(report.failed, 1)
        self.assertEqual(report.passed, 1)
        self.assertEqual(gate(report), 1)

    def test_missing_suites_rejected(self):
        raw = sample_report()
        raw.pop("suites")
        with self.assertRaises(E2EError):
            parse_report(json.dumps(raw))

    def test_elapsed_negative_rejected(self):
        raw = sample_report()
        raw["suites"][0]["results"][0]["elapsedMs"] = -3
        with self.assertRaises(E2EError):
            parse_report(json.dumps(raw))


class GateTests(unittest.TestCase):
    def test_gate_zero_on_pass(self):
        report = parse_report(json.dumps(sample_report()))
        report = type(report)(report.schema, report.meta,
                              [SuiteResult("s", "s", list(report.results))])
        self.assertEqual(gate(report), 1)

    def test_gate_zero_when_nothing_failed(self):
        raw = sample_report()
        raw["suites"][0]["results"] = [raw["suites"][0]["results"][0]]
        report = parse_report(json.dumps(raw))
        self.assertEqual(gate(report), 0)

    def test_human_summary_lists_failure_and_fix(self):
        report = parse_report(json.dumps(sample_report()))
        text = human_summary(report)
        self.assertIn("1 failed", text)
        self.assertIn("World authoring available", text)
        self.assertIn("BindingCatalog", text)

    def test_load_report_reads_file(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / REPORT_FILENAME
            path.write_text(json.dumps(sample_report()))
            report = load_report(path)
            self.assertEqual(report.failed, 1)
        with self.assertRaises(E2EError):
            load_report(Path(td) / "missing.json")


class IsolationTests(unittest.TestCase):
    def make_real(self, base):
        real = base / "real-saves"
        real.mkdir()
        (real / "slot1.json").write_text("{\"progress\": 7}")
        (real / "meta.txt").write_text("checksum=x")
        return real

    def test_tree_manifest_is_stable_hash_map(self):
        with tempfile.TemporaryDirectory() as td:
            real = self.make_real(Path(td))
            self.assertEqual(len(tree_manifest(real)), 2)
            self.assertEqual(
                tree_manifest(real),
                tree_manifest(real),
                msg="manifest must be deterministic",
            )
            original = tree_manifest(real)
            (real / "slot1.json").write_text("changed")
            self.assertNotEqual(original, tree_manifest(real))

    def test_unchanged_guard_passes(self):
        with tempfile.TemporaryDirectory() as td:
            real = self.make_real(Path(td))
            before = tree_manifest(real)
            assert_real_saves_unchanged(before, real)  # no error

    def test_changed_guard_raises(self):
        with tempfile.TemporaryDirectory() as td:
            real = self.make_real(Path(td))
            before = tree_manifest(real)
            (real / "slot1.json").write_text("corrupted by runaway harness")
            with self.assertRaises(SaveGuardError):
                assert_real_saves_unchanged(before, real)

    def test_disposable_profile_keeps_real_saves_untouched(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td)
            real = self.make_real(base)
            before = tree_manifest(real)
            with DisposableSaveProfile(real_dir=real, workspace=base / "work") as profile:
                self.assertTrue(profile.path.is_dir())
                # The harness may write only inside the disposable profile.
                (profile.path / "state.bin").write_bytes(b"\x01\x02")
            self.assertEqual(tree_manifest(real), before,
                             msg="real saves must be byte-identical after run")
            # The disposable copy and its backup are discarded; the caller-owned
            # workspace parent is left for the outer TemporaryDirectory to clean.
            self.assertFalse(profile.path.exists(),
                             msg="disposable profile copy should be discarded")
            self.assertEqual(list(Path(base / "work").glob("vgmodapi-e2e-*")), [],
                             msg="no leftover backup dirs may remain")

    def test_disposable_profile_rejects_run_that_touches_real_saves(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td)
            real = self.make_real(base)
            with self.assertRaises(SaveGuardError):
                with DisposableSaveProfile(real_dir=real, workspace=base / "work") as profile:
                    # Simulate a broken harness writing to the real save tree.
                    (real / "slot1.json").write_text("overwritten")

    def test_disposable_profile_requires_existing_real_dir(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(E2EError):
                with DisposableSaveProfile(real_dir=Path(td) / "nope",
                                           workspace=Path(td) / "w"):
                    pass


class LaunchAndWaitTests(unittest.TestCase):
    def test_build_launch_command(self):
        with tempfile.TemporaryDirectory() as td:
            game = Path(td) / "game"
            game.mkdir()
            exe = game / "VanguardGalaxy.exe"
            exe.write_bytes(b"MZ")
            cmd = build_launch_command(str(game))
            self.assertEqual(cmd[0], str(exe))
            self.assertIn("-nographics", cmd)
            self.assertIn("-screen-fullscreen", cmd)

    def test_build_launch_command_missing_exe(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(E2EError):
                build_launch_command(str(Path(td) / "no-game"))

    def test_build_launch_env_sets_harness_contract(self):
        env = build_launch_env("/x/report.json")
        self.assertEqual(env["EWTEST_RUN"], "1")
        self.assertEqual(env["EWTEST_REPORT_PATH"], "/x/report.json")
        # The harness must never inherit a stale run flag from the caller.
        env = build_launch_env("/y/report.json", run="1")
        self.assertEqual(env["EWTEST_REPORT_PATH"], "/y/report.json")


    def test_wait_for_report_returns_when_file_appears(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / REPORT_FILENAME
            time.sleep(0.05)
            path.write_text("{}")
            found = wait_for_report(td, timeout_seconds=3, poll_seconds=0.05)
            self.assertEqual(found, path)

    def test_wait_for_report_timeout_returns_none(self):
        with tempfile.TemporaryDirectory() as td:
            found = wait_for_report(td, timeout_seconds=0.2, poll_seconds=0.05)
            self.assertIsNone(found)


class CliTests(unittest.TestCase):
    def test_report_mode_returns_failure_code(self):
        import e2e as module
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / REPORT_FILENAME
            path.write_text(json.dumps(sample_report()))
            code = module.main(["--report", str(path)])
            self.assertEqual(code, 1)

    def test_report_mode_zero_on_all_pass(self):
        import e2e as module
        raw = sample_report()
        raw["suites"][0]["results"] = [raw["suites"][0]["results"][0]]
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / REPORT_FILENAME
            path.write_text(json.dumps(raw))
            code = module.main(["--report", str(path)])
            self.assertEqual(code, 0)

    def test_preview_mode_is_ci_safe_without_game(self):
        # --preview must not require a real game launch: it builds the command and
        # returns 0 after validating wiring. Use a fake exe so build_launch works.
        import e2e as module
        with tempfile.TemporaryDirectory() as td:
            game = Path(td) / "game"
            game.mkdir()
            (game / "VanguardGalaxy.exe").write_bytes(b"MZ")
            code = module.main(["--game-dir", str(game), "--preview"])
            self.assertEqual(code, 0)


if __name__ == "__main__":
    unittest.main()
