"""Validate package layout and create a deterministic archive; assembly checks live in make check-package."""
import argparse
import hashlib
from pathlib import Path, PureWindowsPath
import zipfile

REQUIRED_FILES = {
    'VGModAPI.dll', 'VGModAPI.Core.dll', 'VGModAPI.Abstractions.dll', 'VGModAPI.Unity.dll',
    'README.md', 'LICENSE', 'vgmodapi.vgmod.json',
}


def validate_layout(root: Path):
    if root.is_symlink() or not root.is_dir():
        raise ValueError('Package must be a real directory')
    actual = set()
    windows_paths = set()
    for path in root.rglob('*'):
        relative = path.relative_to(root)
        name = relative.as_posix()
        # Archives are installed on Windows as well as Unix; forbid alternate separators/devices.
        if any(any(c in '<>:"\\|?*' for c in part) or part.endswith((' ', '.'))
               or any(ord(c) < 32 for c in part) or PureWindowsPath(part).is_reserved()
               for part in relative.parts):
            raise ValueError(f'Nonportable package path: {name}')
        if name.casefold() in windows_paths:
            raise ValueError(f'Case-colliding package path: {name}')
        windows_paths.add(name.casefold())
        if path.is_symlink():
            raise ValueError('Package links are forbidden')
        reference = relative.parts[:2] == ('docs', 'reference')
        asset = relative.parts[:2] == ('docs', 'assets')
        if path.is_dir():
            if name != 'docs' and not reference and not asset:
                raise ValueError(f'Unexpected package directory: {name}')
        elif path.is_file():
            if name not in REQUIRED_FILES and not (reference and path.suffix == '.md') and not (asset and path.suffix == '.png'):
                raise ValueError(f'Unexpected package file: {name}')
            actual.add(name)
        else:
            raise ValueError(f'Unsupported package entry: {name}')
    if not REQUIRED_FILES <= actual:
        raise ValueError('Missing package files: ' + ', '.join(sorted(REQUIRED_FILES - actual)))
    if not any(name.startswith('docs/reference/') and name.endswith('.md') for name in actual):
        raise ValueError('Package reference documentation is missing')
    return actual


def create(root: Path, output: Path):
    files = validate_layout(root)
    if root.resolve() in output.resolve().parents or output.is_symlink():
        raise ValueError('Archive must be outside the package and not a link')
    checksum = output.with_suffix(output.suffix + '.sha256')
    if checksum.is_symlink():
        raise ValueError('Checksum must not be a link')
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name in sorted(files):
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
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--output', type=Path)
    mode.add_argument('--validate-only', action='store_true')
    args = parser.parse_args()
    if args.validate_only:
        validate_layout(args.root)
    else:
        print(create(args.root, args.output))
