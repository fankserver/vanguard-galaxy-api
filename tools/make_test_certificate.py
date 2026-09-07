#!/usr/bin/env python3
"""Create a throwaway, untrusted loopback TLS fixture outside the repository. Never import it into a trust store."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile


def create(output):
    if Path(__file__).resolve().parents[1] in output.resolve().parents:
        raise ValueError('Test certificates must remain outside the repository')
    if output.exists() or output.is_symlink() or not output.parent.is_dir():
        raise ValueError('Choose a new file in an existing private directory')
    with tempfile.TemporaryDirectory(prefix='vg-test-tls-') as directory:
        root = Path(directory)
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
                        '-subj', '/CN=localhost', '-keyout', str(root / 'key.pem'), '-out', str(root / 'cert.pem')],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        subprocess.run(['openssl', 'pkcs12', '-export', '-legacy', '-passout', 'pass:', '-inkey', str(root / 'key.pem'),
                        '-in', str(root / 'cert.pem'), '-out', str(root / 'test.pfx')],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        data = (root / 'test.pfx').read_bytes()
        if len(data) > 16384: raise ValueError('Unexpected certificate fixture size')
        fd = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'wb') as stream: stream.write(data)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    create(parser.parse_args().output)
