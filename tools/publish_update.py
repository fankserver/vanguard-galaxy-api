"""Publish an already checked archive before advancing update discovery. Explicit --publish only."""
import argparse
import hashlib
import json
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


def numeric(value):
    if not isinstance(value, str) or not re.fullmatch(r'[0-9]+(?:\.[0-9]+){1,3}', value):
        raise ValueError('Expected two-to-four numeric version segments, without labels')
    parts = [int(x) for x in value.split('.')]
    if any(x > 2147483647 for x in parts):
        raise ValueError('Version segment out of range')
    return tuple(parts + [0] * (4 - len(parts)))


def validate_feed(data, plugin, version, channel, release_url):
    if len(data) > 16384:
        raise ValueError('Oversized feed')
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('Duplicate feed key')
            result[key] = value
        return result
    feed = json.loads(data.decode('utf-8'), object_pairs_hook=unique)
    if (set(feed) != {'schemaVersion', 'pluginId', 'version', 'channel', 'releaseUrl'} or
            type(feed['schemaVersion']) is not int or feed['schemaVersion'] != 1 or
            feed['pluginId'] != plugin or feed['channel'] != channel or
            numeric(feed['version']) != numeric(version) or feed['releaseUrl'] != release_url):
        raise ValueError('Feed context mismatch')
    return feed


def publish(remote, tag, archive, feed_path, plugin, version, channel):
    """Remote adapter calls are intentionally ordered; a retry verifies immutable existing assets."""
    if channel not in ('stable', 'experimental') or tag != 'v' + version + ('-experimental' if channel == 'experimental' else ''):
        raise ValueError('Tag/label/channel mismatch')
    feed_bytes = feed_path.read_bytes()
    release_url = 'https://github.com/' + remote.repo + '/releases/tag/' + tag
    feed = validate_feed(feed_bytes, plugin, version, channel, release_url)
    discovery = remote.latest() if channel == 'stable' else remote.get('updates-experimental')
    if discovery:
        prior = remote.asset(discovery['tagName'], 'update.json')
        if prior:
            previous = json.loads(prior)
            if previous.get('pluginId') != plugin or previous.get('channel') != channel:
                raise ValueError('Existing advertised feed has different identity/channel')
            if numeric(previous['version']) > numeric(feed['version']):
                raise ValueError('Refusing to roll advertised version backwards')
            if numeric(previous['version']) == numeric(feed['version']) and previous.get('releaseUrl') != release_url:
                raise ValueError('Same numeric version cannot advertise a different release')
    release = remote.get(tag)
    if release and bool(release['isPrerelease']) != (channel == 'experimental'):
        raise ValueError('Existing release channel disagrees')
    if not release:
        remote.create(tag, channel == 'experimental')
    # Archive bytes come from a checked package. Public assets are immutable, including on retry.
    archive_bytes = archive.read_bytes()
    checksum = (hashlib.sha256(archive_bytes).hexdigest() + '  ' + archive.name + '\n').encode('ascii')
    for name, content in ((archive.name, archive_bytes), (archive.name + '.sha256', checksum)):
        existing = remote.asset(tag, name)
        if existing is not None and existing != content:
            raise ValueError('Existing archive/checksum differs; use a new version')
        if existing is None:
            remote.upload(tag, name, content, False)
    if remote.get(tag)['isDraft']:
        remote.make_public(tag)  # Never mark latest here: artifacts must precede discovery.
    public = remote.get(tag)
    if not public or public['isDraft'] or remote.asset(tag, archive.name) != archive_bytes:
        raise ValueError('Public archive verification failed; feed not advanced')
    existing_feed = remote.asset(tag, 'update.json')
    if existing_feed is not None and existing_feed != feed_bytes:
        raise ValueError('Immutable per-release feed differs')
    if existing_feed is None:
        remote.upload(tag, 'update.json', feed_bytes, False)
    if remote.asset(tag, 'update.json') != feed_bytes:
        raise ValueError('Release feed verification failed')
    if channel == 'stable':
        remote.promote(tag)
    else:
        rolling = remote.get('updates-experimental')
        if not rolling:
            remote.create('updates-experimental', True, verify_tag=False)
            remote.make_public('updates-experimental')
        elif not rolling['isPrerelease']:
            raise ValueError('Experimental discovery release must be a prerelease')
        elif rolling['isDraft']:
            remote.make_public('updates-experimental')
        # A replace may briefly return 404; it can never announce missing versioned artifacts.
        remote.upload('updates-experimental', 'update.json', feed_bytes, True)


class GitHub:
    def __init__(self, repo):
        if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo):
            raise ValueError('Invalid repository')
        self.repo = repo

    def command(self, *args):
        result = subprocess.run(['gh', *args, '--repo', self.repo], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if result.returncode:
            raise RuntimeError('GitHub release operation failed')
        return result.stdout

    def get(self, tag):
        # Only a confirmed HTTP404 is absence. Auth/network failures must stop publication.
        result = subprocess.run(['gh', 'api', 'repos/' + self.repo + '/releases/tags/' + tag], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if result.returncode:
            if b'HTTP 404' in result.stderr:
                return None
            raise RuntimeError('Release lookup failed')
        item = json.loads(result.stdout)
        return {'tagName': item['tag_name'], 'isDraft': item['draft'], 'isPrerelease': item['prerelease'], 'assets': item['assets']}

    def latest(self):
        result = subprocess.run(['gh', 'api', 'repos/' + self.repo + '/releases/latest'], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if result.returncode:
            if b'HTTP 404' in result.stderr:
                return None
            raise RuntimeError('Latest release lookup failed')
        return self.get(json.loads(result.stdout)['tag_name'])

    def asset(self, tag, name):
        release = self.get(tag)
        if not release or not any(a['name'] == name for a in release['assets']):
            return None
        with tempfile.TemporaryDirectory() as directory:
            self.command('release', 'download', tag, '--pattern', name, '--dir', directory)
            return (Path(directory) / name).read_bytes()

    def create(self, tag, prerelease, verify_tag=True):
        args = ['release', 'create', tag, '--draft', '--latest=false', '--title', tag, '--notes', 'Release artifacts and generated update metadata.']
        if prerelease:
            args.append('--prerelease')
        if verify_tag:
            args.append('--verify-tag')
        self.command(*args)

    def upload(self, tag, name, content, replace):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / name
            path.write_bytes(content)
            args = ['release', 'upload', tag, str(path)]
            if replace:
                args.append('--clobber')
            self.command(*args)

    def make_public(self, tag):
        self.command('release', 'edit', tag, '--draft=false', '--latest=false')

    def promote(self, tag):
        self.command('release', 'edit', tag, '--latest=true')


def execute(args):
    numeric(args.version)
    GitHub(args.repo)  # Validate the repository even for a dry run; no request is made.
    if args.channel not in ('stable', 'experimental') or args.tag != 'v' + args.version + ('-experimental' if args.channel == 'experimental' else ''):
        raise ValueError('Tag/label/channel mismatch')
    validate_archive(args.archive, args.assembly, args.plugin)
    # Generate from packaged compiled metadata, never from development-branch strings alone.
    with tempfile.TemporaryDirectory() as directory:
        feed_path = Path(directory) / 'update.json'
        subprocess.run([args.dotnet, 'run', '--project', str(Path(__file__).parent / 'ReleaseMetadata'), '--', str(args.assembly), args.plugin,
                        args.version, args.channel, 'https://github.com/' + args.repo + '/releases/tag/' + args.tag, str(feed_path)], check=True)
        if not args.publish:
            print('Dry run: packaged metadata validated; no GitHub mutation or feed advertisement.')
        else:
            publish(GitHub(args.repo), args.tag, args.archive, feed_path, args.plugin, args.version, args.channel)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('repo', 'tag', 'plugin', 'version', 'channel'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--assembly', type=Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--publish', action='store_true')
    execute(parser.parse_args())
