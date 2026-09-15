"""Validate an author update package and its compiled metadata; never publish."""
import argparse
from pathlib import Path
import re
import subprocess
import tempfile
import stat
import zipfile
from release_archive import validate_layout


def validate_archive(archive, assembly, plugin):
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]*\.zip', archive.name):
        raise ValueError('Archive needs a plain .zip asset name')
    root = assembly.parent
    prefix = root.name + '/'
    allowed = validate_layout(root) if plugin == 'vgmodapi' else {assembly.name, plugin + '.vgmod.json'}
    with zipfile.ZipFile(archive) as bundle:
        entries = bundle.infolist()
        if len(entries) != len(allowed) or {e.filename for e in entries} != {prefix + name for name in allowed}:
            raise ValueError('Release archive allowlist mismatch')
        if sum(e.file_size for e in entries) > 32 * 1024 * 1024:
            raise ValueError('Release archive is oversized')
        for entry in entries:
            if entry.is_dir() or stat.S_ISLNK(entry.external_attr >> 16) or entry.file_size > 8 * 1024 * 1024:
                raise ValueError('Invalid archive entry')
            local = root / entry.filename[len(prefix):]
            if local.is_symlink() or any(p.is_symlink() for p in local.parents if p != root.parent):
                raise ValueError('Package links are forbidden')
            with bundle.open(entry) as stream:
                content = stream.read(8 * 1024 * 1024 + 1)
            if len(content) != entry.file_size or local.stat().st_size != len(content) or local.read_bytes() != content:
                raise ValueError('Archive differs from packaged metadata source')


def execute(args):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', args.repo):
        raise ValueError('Invalid repository')
    if not re.fullmatch(r'[0-9]+(?:\.[0-9]+){1,3}', args.version):
        raise ValueError('Expected a numeric version')
    if args.channel not in ('stable', 'experimental') or args.tag != 'v' + args.version + ('-experimental' if args.channel == 'experimental' else ''):
        raise ValueError('Tag/label/channel mismatch')
    validate_archive(args.archive, args.assembly, args.plugin)
    # Generate from packaged compiled metadata, never from development-branch strings alone.
    with tempfile.TemporaryDirectory() as directory:
        feed_path = Path(directory) / 'update.json'
        subprocess.run([args.dotnet, 'run', '--project', str(Path(__file__).parent / 'ReleaseMetadata'), '--', str(args.assembly), args.plugin,
                        args.version, args.channel, 'https://github.com/' + args.repo + '/releases/tag/' + args.tag, str(feed_path)], check=True)
        print('Package and compiled update metadata validated; no GitHub changes.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('repo', 'tag', 'plugin', 'version', 'channel'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--assembly', type=Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    execute(parser.parse_args())
