import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).with_name('release.sh').resolve()


class ReleaseTests(unittest.TestCase):
    def run_release(self, tag='v0.2.8', prerelease='true', dirty='', head='commit', upload_status=0):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'artifacts').mkdir()
            archive = root / 'artifacts/VGModAPI-0.2.8-stable.zip'
            archive.write_bytes(b'checked package')
            commands = {
                'git': 'case "$*" in "rev-parse HEAD") echo "$HEAD";; rev-parse*) echo commit;; status*) printf "%s" "$DIRTY";; esac',
                'gh': 'echo "gh $*" >> "$LOG"; if [[ $1 == release && $2 == view ]]; then echo "$PRERELEASE"; else exit "$UPLOAD_STATUS"; fi',
                'make': 'echo "make $*" >> "$LOG"',
            }
            for name, body in commands.items():
                path = root / name
                path.write_text('#!/bin/bash\n' + body + '\n')
                path.chmod(0o755)
            log = root / 'commands'
            result = subprocess.run(['bash', str(SCRIPT), tag], cwd=root, capture_output=True, text=True,
                env=dict(os.environ, PATH=str(root) + ':' + os.environ['PATH'], LOG=str(log),
                         PRERELEASE=prerelease, DIRTY=dirty, HEAD=head, UPLOAD_STATUS=str(upload_status)))
            return result, log.read_text() if log.exists() else '', (archive.with_suffix('.zip.sha256').exists())

    def test_builds_numeric_tag_and_uploads_only_archive_and_checksum(self):
        result, log, checksum = self.run_release()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('make check-local CONFIGURATION=Release RELEASE_VERSION=0.2.8 RELEASE_CHANNEL=stable', log)
        self.assertIn('make release-archive CONFIGURATION=Release RELEASE_VERSION=0.2.8 RELEASE_CHANNEL=stable', log)
        self.assertIn('gh release upload v0.2.8 artifacts/VGModAPI-0.2.8-stable.zip artifacts/VGModAPI-0.2.8-stable.zip.sha256', log)
        self.assertTrue(checksum)
        for forbidden in ('--clobber', 'update.json', 'release create', 'release edit'):
            self.assertNotIn(forbidden, log)

    def test_refuses_invalid_tag_wrong_checkout_dirty_tree_and_non_prerelease(self):
        for args in ({'tag': 'v0.2.8-experimental'}, {'tag': '--help'}, {'head': 'different'},
                     {'dirty': ' M tools/release.sh'}, {'prerelease': 'false'}):
            with self.subTest(args=args):
                result, log, checksum = self.run_release(**args)
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn('make ', log)
                self.assertNotIn('gh release upload', log)
                self.assertFalse(checksum)

    def test_upload_failure_is_reported_without_retry_or_overwrite(self):
        result, log, _ = self.run_release(upload_status=1)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(log.count('gh release upload'), 1)


if __name__ == '__main__':
    unittest.main()
