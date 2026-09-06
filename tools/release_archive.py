"""Create a deterministic, owned-files-only archive after make check-package."""
import argparse
import hashlib
from pathlib import Path
import zipfile

FILES = {
    'VGModAPI.dll', 'VGModAPI.Core.dll', 'VGModAPI.Abstractions.dll',
    'README.md', 'LICENSE',
    *('docs/' + name + '.md' for name in (
        'checks', 'compatibility', 'implementation-plan', 'lifecycle-contract',
        'qualification-runner', 'research-findings', 'persistence-identity', 'persistence-schema', 'persistence-storage')),
}


def create(root: Path, output: Path):
    if root.is_symlink() or not root.is_dir():
        raise ValueError('Package must be a real directory')
    entries = list(root.rglob('*'))
    if any(p.is_symlink() for p in entries):
        raise ValueError('Package links are forbidden')
    if any(p.is_dir() and p.relative_to(root).as_posix() != 'docs' for p in entries):
        raise ValueError('Unexpected package directory')
    actual = {p.relative_to(root).as_posix() for p in entries if p.is_file()}
    if actual != FILES or any(not p.is_file() and not p.is_dir() for p in entries):
        raise ValueError('Package allowlist mismatch')
    if root.resolve() in output.resolve().parents or output.is_symlink():
        raise ValueError('Archive must be outside the package and not a link')
    checksum = output.with_suffix(output.suffix + '.sha256')
    if checksum.is_symlink():
        raise ValueError('Checksum must not be a link')
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name in sorted(FILES):
            info = zipfile.ZipInfo('VGModAPI/' + name, (1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            archive.writestr(info, (root / name).read_bytes())
    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    checksum.write_text(f'{digest}  {output.name}\n', encoding='ascii')
    return digest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    print(create(args.root, args.output))
