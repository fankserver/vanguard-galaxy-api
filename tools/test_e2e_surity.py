import os
import stat
import tempfile
import unittest
from pathlib import Path

from e2e_surity import build_command, gate_exit, main


class SurityUsageTests(unittest.TestCase):
    def test_build_command_uses_surity_and_game_exe(self):
        with tempfile.TemporaryDirectory() as td:
            cmd = build_command("surity", td, "VanguardGalaxy.exe")
            self.assertEqual(cmd[0], "surity")
            self.assertEqual(cmd[1], str(Path(td) / "VanguardGalaxy.exe"))

    def test_gate_exit_maps_surity_codes(self):
        self.assertEqual(gate_exit(0), 0)  # all passed
        self.assertEqual(gate_exit(1), 1)  # test failures
        self.assertEqual(gate_exit(2), 1)  # exit requested

    def test_preview_is_ci_safe(self):
        # --preview must not require a game or an installed Surity runner.
        with tempfile.TemporaryDirectory() as td:
            code = main(["--game-dir", td, "--preview", "--surity", "surity"])
            self.assertEqual(code, 0)

    def test_missing_surity_runner_gates_as_failure(self):
        # Point at a nonexistent runner + real save dir; the driver must fail cleanly,
        # NOT modify the real save tree (the guard must be satisfied).
        with tempfile.TemporaryDirectory() as td:
            base = Path(td)
            game = base / "game"; game.mkdir()
            saves = base / "saves"; saves.mkdir()
            (saves / "slot.json").write_text("x")
            code = main(["--game-dir", str(game), "--surity", str(base / "no-such-surity"),
                         "--save-dir", str(saves)])
            self.assertEqual(code, 1)
            self.assertEqual((saves / "slot.json").read_text(), "x")
            self.assertEqual(list(base.glob("vgmodapi-surity-*")), [])


if __name__ == "__main__":
    unittest.main()
