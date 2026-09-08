"""Validate the measured assembly scope and render a dependency-free coverage badge."""
import json
from pathlib import Path
import shutil
import sys
import xml.etree.ElementTree as ET

EXPECTED = {"VGModAPI.Core", "VGModAPI.Abstractions"}


def generate(directory):
    directory = Path(directory)
    reports = list((directory / "raw").glob("*/coverage.cobertura.xml"))
    if len(reports) != 1:
        raise ValueError("Expected exactly one fresh Cobertura report")
    root = ET.parse(reports[0]).getroot()
    packages = root.findall("./packages/package")
    if len(packages) != 2 or {p.get("name") for p in packages} != EXPECTED:
        raise ValueError("Coverage must contain exactly Core and Abstractions")
    counts = {}
    for kind in ("lines", "branches"):
        covered, valid = (int(root.attrib[f"{kind}-{key}"]) for key in ("covered", "valid"))
        if not 0 <= covered <= valid or valid <= 0:
            raise ValueError(f"Invalid or empty {kind} coverage")
        counts[kind] = {"covered": covered, "valid": valid, "percent": 100 * covered / valid}
    percent = counts["lines"]["percent"]
    label = "Core + Abstractions coverage"
    value = f"{percent:.2f}%"
    color = "#4c1" if percent >= 80 else "#dfb317" if percent >= 60 else "#e05d44"
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="20" role="img" aria-label="{label}: {value}">
<title>{label}: {value}</title>
<rect width="256" height="20" rx="3" fill="#555"/>
<path fill="{color}" d="M198 0h55a3 3 0 0 1 3 3v14a3 3 0 0 1-3 3h-55z"/>
<g fill="#fff" text-anchor="middle" font-family="Verdana,DejaVu Sans,sans-serif" font-size="11">
<text x="99" y="14">{label}</text><text x="227" y="14">{value}</text>
</g></svg>
'''
    (directory / "badge.svg").write_text(svg, encoding="utf-8")
    (directory / "summary.json").write_text(json.dumps(counts, indent=2) + "\n", encoding="utf-8")
    shutil.copyfile(reports[0], directory / "coverage.cobertura.xml")
    print(f"{label}: {value} lines, {counts['branches']['percent']:.2f}% branches")


if __name__ == "__main__":
    generate(sys.argv[1])
