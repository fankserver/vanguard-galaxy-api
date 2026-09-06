import tempfile
from pathlib import Path
import unittest
import zipfile
from release_archive import FILES, create


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.root = self.base / 'package'
        for name in FILES:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode())
        self.output = self.base / 'release.zip'

    def test_repeatable_owned_layout(self):
        first = create(self.root, self.output)
        self.assertEqual(first, create(self.root, self.output))
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(set(archive.namelist()), {'VGModAPI/' + n for n in FILES})
            for name in FILES:
                self.assertEqual(archive.read('VGModAPI/' + name), name.encode())
        self.assertEqual(self.output.with_suffix('.zip.sha256').read_text(), first + '  release.zip\n')

    def test_missing_or_extra_refused(self):
        (self.root / 'Assembly-CSharp.dll').write_bytes(b'not distributable')
        with self.assertRaises(ValueError):
            create(self.root, self.output)
        (self.root / 'Assembly-CSharp.dll').unlink()
        (self.root / 'LICENSE').unlink()
        with self.assertRaises(ValueError):
            create(self.root, self.output)

    def test_links_refused(self):
        (self.root / 'LICENSE').unlink()
        (self.root / 'LICENSE').symlink_to(self.root / 'README.md')
        with self.assertRaises(ValueError):
            create(self.root, self.output)

    def test_output_inside_package_refused(self):
        with self.assertRaises(ValueError):
            create(self.root, self.root / 'release.zip')


if __name__ == '__main__':
    unittest.main()
