import os
import tempfile
from pathlib import Path
import unittest
import zipfile
from release_archive import REQUIRED_FILES, create, validate_layout


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.root = self.base / 'package'
        self.files = REQUIRED_FILES | {'docs/reference/example.md', 'docs/assets/logo.png'}
        for name in self.files:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode())
        self.output = self.base / 'release.zip'

    def test_repeatable_owned_layout(self):
        first = create(self.root, self.output)
        self.assertEqual(first, create(self.root, self.output))
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(set(archive.namelist()), {'VGModAPI/' + n for n in self.files})
            for name in self.files:
                self.assertEqual(archive.read('VGModAPI/' + name), name.encode())
        self.assertEqual(self.output.with_suffix('.zip.sha256').read_text(), first + '  release.zip\n')

    def test_document_names_are_not_an_allowlist(self):
        original = self.root / 'docs/reference/example.md'
        renamed = original.with_name('renamed.md')
        original.rename(renamed)
        nested = self.root / 'docs/reference/topic/new.md'
        nested.parent.mkdir()
        nested.write_text('new public contract')
        files = validate_layout(self.root)
        self.assertIn('docs/reference/renamed.md', files)
        self.assertIn('docs/reference/topic/new.md', files)
        create(self.root, self.output)

    def test_missing_required_files_refused(self):
        for name in REQUIRED_FILES:
            with self.subTest(name=name):
                path = self.root / name
                data = path.read_bytes()
                path.unlink()
                with self.assertRaises(ValueError):
                    validate_layout(self.root)
                path.write_bytes(data)

    def test_missing_reference_docs_refused(self):
        (self.root / 'docs/reference/example.md').unlink()
        with self.assertRaises(ValueError):
            validate_layout(self.root)

    def test_unexpected_files_refused(self):
        for name in ('Assembly-CSharp.dll', 'UnityEngine.dll', 'BepInEx.dll', '0Harmony.dll',
                     'QualificationRunner.dll', 'old-build.pdb', 'unlisted.md',
                     'docs/reference/Assembly-CSharp.dll', 'docs/reference/nested/UnityEngine.dll',
                     'docs/assets/BepInEx.dll', 'docs/assets/script.js', 'docs/development/guide.md'):
            with self.subTest(name=name):
                path = self.root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'not distributable')
                with self.assertRaises(ValueError):
                    create(self.root, self.output)
                path.unlink()
                if name.startswith('docs/development/'):
                    path.parent.rmdir()

    def test_unexpected_empty_directories_refused(self):
        for name in ('lib', 'docs/development', 'docs/private'):
            with self.subTest(name=name):
                path = self.root / name
                path.mkdir()
                with self.assertRaises(ValueError):
                    validate_layout(self.root)
                path.rmdir()

    @unittest.skipIf(os.name == 'nt', 'Windows symlink creation may require privileges')
    def test_links_refused(self):
        for name, target in (('linked-root', self.root), ('package/docs/reference/link', self.base),
                             ('package/docs/reference/link.md', self.root / 'README.md')):
            with self.subTest(name=name):
                link = self.base / name
                link.symlink_to(target, target_is_directory=target.is_dir())
                with self.assertRaises(ValueError):
                    validate_layout(link if name == 'linked-root' else self.root)
                link.unlink()

    @unittest.skipIf(os.name == 'nt', 'FIFO is a Unix file type')
    def test_nonregular_entries_refused(self):
        os.mkfifo(self.root / 'docs/reference/pipe.md')
        with self.assertRaises(ValueError):
            validate_layout(self.root)

    @unittest.skipIf(os.name == 'nt', 'Names are only creatable on Unix')
    def test_nonportable_archive_paths_refused(self):
        for name in ('..\\escape.md', 'topic:stream.md', 'NUL.md', 'trailing.', 'trailing ', 'line\nfeed.md'):
            with self.subTest(name=name):
                path = self.root / 'docs/reference' / name
                path.write_text('not portable')
                with self.assertRaises(ValueError):
                    create(self.root, self.output)
                path.unlink()

    def test_output_inside_package_refused(self):
        with self.assertRaises(ValueError):
            create(self.root, self.root / 'release.zip')

    @unittest.skipIf(os.name == 'nt', 'Windows symlink creation may require privileges')
    def test_output_and_checksum_links_refused(self):
        for path in (self.output, self.output.with_suffix('.zip.sha256')):
            with self.subTest(path=path):
                path.symlink_to(self.root / 'README.md')
                with self.assertRaises(ValueError):
                    create(self.root, self.output)
                path.unlink()


if __name__ == '__main__':
    unittest.main()
