import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace
import subprocess
import zipfile
from publish_update import publish, validate_feed, validate_archive, execute, GitHub
from local_update_metadata import generate
from package_update_example import package


class Remote:
    repo = 'example/mod'
    def __init__(self, fail=None):
        self.releases = {}
        self.current = None
        self.events = []
        self.fail = fail
    def event(self, operation, tag, name=''):
        event = (operation, tag, name)
        self.events.append(event)
        if event == self.fail:
            self.fail = None
            raise RuntimeError('injected publication failure')
    def latest(self): return self.get(self.current)
    def get(self, tag): return self.releases.get(tag)
    def asset(self, tag, name): return self.releases.get(tag, {}).get('assets', {}).get(name)
    def create(self, tag, prerelease, verify_tag=True):
        self.event('create', tag)
        self.releases[tag] = dict(tagName=tag, isDraft=True, isPrerelease=prerelease, assets={})
    def upload(self, tag, name, content, replace):
        self.event('upload', tag, name)
        if not replace and name in self.releases[tag]['assets']: raise ValueError('immutable asset')
        self.releases[tag]['assets'][name] = content
    def make_public(self, tag):
        self.event('public', tag)
        self.releases[tag]['isDraft'] = False
    def promote(self, tag):
        self.event('promote', tag)
        if self.releases[tag]['isDraft']: raise ValueError('draft promotion')
        self.current = tag


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.archive = self.root / 'mod.zip'
        self.archive.write_bytes(b'checked archive fixture')
        self.feed = self.root / 'update.json'
    def tearDown(self): self.temp.cleanup()
    def run_release(self, remote, channel='stable', version='1.2.3'):
        tag = 'v' + version + ('-experimental' if channel == 'experimental' else '')
        self.feed.write_text(json.dumps(dict(schemaVersion=1, pluginId='example.mod', version=version, channel=channel,
            releaseUrl='https://github.com/example/mod/releases/tag/' + tag)))
        publish(remote, tag, self.archive, self.feed, 'example.mod', version, channel)
        return tag
    def test_archive_and_public_page_precede_feed_and_latest(self):
        remote = Remote(); tag = self.run_release(remote)
        events = remote.events
        self.assertLess(events.index(('upload', tag, 'mod.zip')), events.index(('public', tag, '')))
        self.assertLess(events.index(('public', tag, '')), events.index(('upload', tag, 'update.json')))
        self.assertEqual(events[-1], ('promote', tag, ''))
        self.run_release(remote)  # Same bytes are safe on retry.
        self.assertEqual(sum(e == ('upload', tag, 'mod.zip') for e in remote.events), 1)
    def test_every_partial_failure_is_retryable_without_early_advertisement(self):
        tag = 'v1.2.3'
        for event in [('create', tag, ''), ('upload', tag, 'mod.zip'), ('upload', tag, 'mod.zip.sha256'),
                      ('public', tag, ''), ('upload', tag, 'update.json'), ('promote', tag, '')]:
            with self.subTest(event=event):
                remote = Remote(event)
                with self.assertRaises(RuntimeError): self.run_release(remote)
                self.assertIsNone(remote.latest())
                self.run_release(remote)
                self.assertEqual(remote.current, tag)
    def test_experimental_discovery_advances_last_and_recovers_draft(self):
        for event in [('create', 'updates-experimental', ''), ('public', 'updates-experimental', ''),
                      ('upload', 'updates-experimental', 'update.json')]:
            with self.subTest(event=event):
                remote = Remote(event)
                with self.assertRaises(RuntimeError): self.run_release(remote, 'experimental')
                self.assertIsNone(remote.asset('updates-experimental', 'update.json'))
                self.run_release(remote, 'experimental')
                self.assertIsNone(remote.latest())
                self.assertEqual(remote.events[-1], ('upload', 'updates-experimental', 'update.json'))
    def test_immutable_archive_and_advertised_version_cannot_go_backwards(self):
        remote = Remote(); self.run_release(remote)
        self.archive.write_bytes(b'changed')
        with self.assertRaises(ValueError): self.run_release(remote)
        self.archive.write_bytes(b'checked archive fixture')
        with self.assertRaises(ValueError): self.run_release(remote, version='1.2.2')
    def test_same_numeric_version_cannot_move_to_different_release(self):
        remote = Remote(); self.run_release(remote)
        with self.assertRaises(ValueError): self.run_release(remote, version='1.2.3.0')
    def test_metadata_tool_rejection_occurs_before_remote_operations(self):
        args = SimpleNamespace(repo='example/mod', tag='v1.2.3', plugin='author.mod', version='1.2.3', channel='stable',
            archive=self.archive, assembly=self.root / 'plugin.dll', dotnet='dotnet', publish=True)
        with patch('publish_update.validate_archive'), patch('publish_update.subprocess.run', side_effect=subprocess.CalledProcessError(1, 'metadata-validator')) as run:
            with self.assertRaises(subprocess.CalledProcessError): execute(args)
            self.assertEqual(run.call_count, 1)
            self.assertEqual(run.call_args.args[0][0], 'dotnet')  # No gh lookup/upload after sidecar rejection.
    def test_github_lookup_errors_and_publication_flags(self):
        github = GitHub('example/mod')
        for error in (b'HTTP 401', b'TLS failure', b'connection reset'):
            with patch('publish_update.subprocess.run', return_value=SimpleNamespace(returncode=1, stderr=error, stdout=b'')):
                with self.assertRaises(RuntimeError): github.get('v1.0')
                with self.assertRaises(RuntimeError): github.latest()
        with patch('publish_update.subprocess.run', return_value=SimpleNamespace(returncode=1, stderr=b'HTTP 404', stdout=b'')):
            self.assertIsNone(github.get('v1.0')); self.assertIsNone(github.latest())
        with patch('publish_update.subprocess.run', return_value=SimpleNamespace(returncode=0, stderr=b'', stdout=b'')) as run:
            github.create('v1.0', False)
            self.assertIn('--draft', run.call_args.args[0]); self.assertIn('--verify-tag', run.call_args.args[0]); self.assertIn('--latest=false', run.call_args.args[0])
            github.make_public('v1.0')
            self.assertIn('--draft=false', run.call_args.args[0]); self.assertIn('--latest=false', run.call_args.args[0])
            github.promote('v1.0'); self.assertIn('--latest=true', run.call_args.args[0])
    def test_schema_wrong_guid_and_version_are_rejected_before_publication(self):
        remote = Remote(); self.run_release(remote)
        feed = json.loads(self.feed.read_text())
        for field, value in [('pluginId', 'other'), ('version', '1.2.4'), ('schemaVersion', True), ('channel', 'experimental')]:
            changed = dict(feed); changed[field] = value
            with self.assertRaises(ValueError):
                validate_feed(json.dumps(changed).encode(), 'example.mod', '1.2.3', 'stable', feed['releaseUrl'])
    def test_local_discovery_urls_use_explicit_channels_without_versions(self):
        source = self.root / 'source.json'; output = self.root / 'local.json'
        source.write_text(json.dumps(dict(schemaVersion=1, pluginId='vgmodapi', description='example')))
        for channel, route in [('stable', 'latest/download'), ('experimental', 'download/updates-experimental')]:
            generate(source, output, channel)
            data = json.loads(output.read_text())
            self.assertEqual(data['channel'], channel)
            self.assertIn(route + '/update.json', data['updateUrl'])
            self.assertNotIn('version', data)
        with self.assertRaises(ValueError): generate(source, output, 'beta')
    def test_example_package_contains_only_owned_plugin_and_metadata(self):
        dll = self.root / 'input.dll'; dll.write_bytes(b'owned example')
        metadata = self.root / 'input.json'; metadata.write_text('{"schemaVersion":1,"pluginId":"vgmodapi.example.updates","channel":"stable","updateUrl":"https://github.com/example/mod/releases/latest/download/update.json"}')
        output = self.root / 'UpdateParticipant'
        archive = package(dll, metadata, output)
        validate_archive(archive, output / 'UpdateParticipant.dll', 'vgmodapi.example.updates')
        self.assertEqual(len(list(output.iterdir())), 2)
        (output / 'VGModAPI.Abstractions.dll').write_bytes(b'not distributable here')
        with self.assertRaises(ValueError): package(dll, metadata, output)
    def test_archive_must_match_actual_package_and_forbid_contract_redistribution(self):
        root = self.root / 'Example'; root.mkdir()
        dll = root / 'Example.dll'; dll.write_bytes(b'owned synthetic assembly')
        (root / 'example.mod.vgmod.json').write_text('{}')
        def bundle(extra=False):
            with zipfile.ZipFile(self.archive, 'w') as archive:
                for path in root.iterdir(): archive.write(path, 'Example/' + path.name)
                if extra: archive.writestr('Example/VGModAPI.Abstractions.dll', b'forbidden')
        bundle(); validate_archive(self.archive, dll, 'example.mod')
        dll.write_bytes(b'different')
        with self.assertRaises(ValueError): validate_archive(self.archive, dll, 'example.mod')
        bundle(True)
        with self.assertRaises(ValueError): validate_archive(self.archive, dll, 'example.mod')


if __name__ == '__main__': unittest.main()
