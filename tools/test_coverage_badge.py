import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

from coverage_badge import generate


class CoverageBadgeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.report = self.directory / "raw" / "run" / "coverage.cobertura.xml"
        self.report.parent.mkdir(parents=True)
        self.xml = ('<coverage lines-covered="88" lines-valid="100" '
                    'branches-covered="72" branches-valid="100"><packages>'
                    '<package name="VGModAPI.Core"/>'
                    '<package name="VGModAPI.Abstractions"/>'
                    '</packages></coverage>')
        self.report.write_text(self.xml)

    def test_generates_badge_summary_and_report(self):
        generate(self.directory)
        svg = ET.parse(self.directory / "badge.svg").getroot()
        self.assertIn("88.00%", svg.attrib["aria-label"])
        self.assertIn("Core + Abstractions", svg.attrib["aria-label"])
        summary = json.loads((self.directory / "summary.json").read_text())
        self.assertEqual(72, summary["branches"]["percent"])
        self.assertEqual(self.xml, (self.directory / "coverage.cobertura.xml").read_text())

    def test_rejects_missing_or_multiple_reports(self):
        self.report.unlink()
        with self.assertRaises(ValueError):
            generate(self.directory)
        for name in ("one", "two"):
            path = self.directory / "raw" / name / self.report.name
            path.parent.mkdir()
            path.write_text(self.xml)
        with self.assertRaises(ValueError):
            generate(self.directory)

    def test_rejects_empty_invalid_or_wrong_scope(self):
        for before, after in (( 'lines-valid="100"', 'lines-valid="0"'),
                              ('branches-covered="72"', 'branches-covered="101"'),
                              ('lines-covered="88"', 'lines-covered="-1"'),
                              ('VGModAPI.Core', 'VGModAPI.Tests')):
            with self.subTest(after=after):
                self.report.write_text(self.xml.replace(before, after))
                with self.assertRaises(ValueError):
                    generate(self.directory)
                self.assertFalse((self.directory / "badge.svg").exists())


if __name__ == "__main__":
    unittest.main()
