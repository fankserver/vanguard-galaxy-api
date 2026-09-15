import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace
import subprocess
import zipfile
from validate_update_package import validate_archive, execute
from local_update_metadata import generate
from package_update_example import package
from release_archive import REQUIRED_FILES, create


class UpdatePackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.archive = self.root / 'mod.zip'
        self.archive.write_bytes(b'checked archive fixture')
        self.feed = self.root / 'update.json'
    def tearDown(self): self.temp.cleanup()
    def test_metadata_tool_rejection_occurs_before_remote_operations(self):
        args = SimpleNamespace(repo='example/mod', tag='v1.2.3', plugin='author.mod', version='1.2.3', channel='stable',
            archive=self.archive, assembly=self.root / 'plugin.dll', dotnet='dotnet', publish=True)
        with patch('validate_update_package.validate_archive'), patch('validate_update_package.subprocess.run', side_effect=subprocess.CalledProcessError(1, 'metadata-validator')) as run:
            with self.assertRaises(subprocess.CalledProcessError): execute(args)
            self.assertEqual(run.call_count, 1)
            self.assertEqual(run.call_args.args[0][0], 'dotnet')  # No gh lookup/upload after sidecar rejection.
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
    def test_api_archive_uses_shared_layout_validation(self):
        root = self.root / 'VGModAPI'
        for name in REQUIRED_FILES | {'docs/reference/new-topic.md', 'docs/assets/logo.png'}:
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode())
        create(root, self.archive)
        validate_archive(self.archive, root / 'VGModAPI.dll', 'vgmodapi')
        (root / 'docs/reference/new-topic.md').write_text('changed after archiving')
        with self.assertRaises(ValueError):
            validate_archive(self.archive, root / 'VGModAPI.dll', 'vgmodapi')
        create(root, self.archive)
        private = root / 'docs/development/private.md'
        private.parent.mkdir()
        private.write_text('not for distribution')
        with self.assertRaises(ValueError):
            validate_archive(self.archive, root / 'VGModAPI.dll', 'vgmodapi')

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
