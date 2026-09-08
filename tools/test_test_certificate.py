import tempfile
from pathlib import Path
import unittest
from make_test_certificate import create


class CertificateTests(unittest.TestCase):
    def test_ephemeral_fixture_is_bounded_and_never_overwrites(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'test.pfx'
            create(output)
            self.assertGreater(output.stat().st_size, 0)
            self.assertLessEqual(output.stat().st_size, 16384)
            original = output.read_bytes()
            with self.assertRaises(ValueError): create(output)
            self.assertEqual(original, output.read_bytes())
        with self.assertRaises(ValueError): create(Path(__file__).parent / 'must-not-create.pfx')
