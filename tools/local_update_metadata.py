"""Generate packaged discovery URLs for the explicitly selected release channel."""
import json
from pathlib import Path
import sys


def generate(source, output, channel):
    if channel not in ('stable', 'experimental'):
        raise ValueError('Unknown release channel')
    metadata = json.loads(source.read_text(encoding='utf-8'))
    if metadata.get('pluginId') != 'vgmodapi' or metadata.get('schemaVersion') != 1 or 'version' in metadata:
        raise ValueError('Invalid official package metadata')
    metadata['channel'] = channel
    metadata['updateUrl'] = 'https://github.com/fankserver/vanguard-galaxy-api/releases/' + (
        'latest/download/update.json' if channel == 'stable' else 'download/updates-experimental/update.json')
    output.write_text(json.dumps(metadata, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__': generate(Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3])
