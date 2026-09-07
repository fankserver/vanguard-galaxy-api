"""Package only the example plugin and its declarative metadata, never API/loader DLLs."""
from pathlib import Path
import shutil
import sys
import zipfile


def package(dll, metadata, output):
    output.mkdir(parents=True, exist_ok=True)
    names = {'UpdateParticipant.dll', 'vgmodapi.example.updates.vgmod.json'}
    if output.is_symlink() or any(p.is_symlink() or not p.is_file() or p.name not in names for p in output.iterdir()):
        raise ValueError('Unexpected example package contents')
    shutil.copyfile(dll, output / 'UpdateParticipant.dll')
    shutil.copyfile(metadata, output / 'vgmodapi.example.updates.vgmod.json')
    archive = output.with_suffix('.zip')
    if archive.is_symlink():
        raise ValueError('Archive must not be a link')
    with zipfile.ZipFile(archive, 'w') as bundle:
        for name in sorted(names):
            info = zipfile.ZipInfo(output.name + '/' + name, (1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            bundle.writestr(info, (output / name).read_bytes())
    return archive


if __name__ == '__main__':
    package(*(Path(value) for value in sys.argv[1:]))
