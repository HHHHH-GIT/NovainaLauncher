"""Package public source only; no credentials, game/runtime files or commercial audio."""
from pathlib import Path
import hashlib
import json
import os
import sys
import xml.etree.ElementTree as ET
import zipfile

if sys.version_info < (3, 11):
    raise SystemExit('Python 3.11+ is required')

root = Path(__file__).resolve().parents[1]
version = ET.parse(root / 'src/Launcher.App/Launcher.App.csproj').findtext('./PropertyGroup/Version')
excluded = {'bin', 'obj', 'data', 'runtime', 'logs', '.minecraft', '.git', '.vs',
            'artifacts', 'testresults', '__pycache__', 'node_modules', 'output', '.cache'}
private_suffixes = {'.protected', '.user', '.suo', '.pyc', '.pdb', '.exe', '.dll', '.db'}
private_names = {'assumptions.mp3', 'launcher-paths.json', '.env', 'thumbs.db', '.ds_store'}
root_files = ('README.md', 'AGENTS.md', 'LICENSE', '.gitignore', '.gitattributes',
              'global.json', 'iKunLauncherNext.slnx')

def public_file(path: Path) -> bool:
    relative = path.relative_to(root)
    return (path.is_file() and not path.is_symlink()
            and not any(part.lower() in excluded for part in relative.parts[:-1])
            and path.suffix.lower() not in private_suffixes
            and path.name.lower() not in private_names
            and not path.name.startswith('.env.') and not path.name.endswith('.local.json'))

files = [root / name for name in root_files]
for folder in ('src', 'tests', 'docs', 'spec', 'scripts', '.github', 'promo'):
    directory = root / folder
    if directory.exists():
        for current, directories, names in os.walk(directory, followlinks=False):
            directories[:] = [name for name in directories if name.lower() not in excluded
                              and not (Path(current) / name).is_symlink()]
            files.extend(path for name in names if public_file(path := Path(current) / name))
files = sorted(set(files), key=lambda path: path.relative_to(root).as_posix())
output = root / 'bin'
output.mkdir(exist_ok=True)
source = output / f'NovainaLauncher-source-{version}.zip'
temporary = source.with_suffix('.zip.tmp')
try:
    with zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in files:
            archive.write(path, path.relative_to(root).as_posix())
    with zipfile.ZipFile(temporary) as archive:
        if archive.testzip() is not None:
            raise RuntimeError('Source archive validation failed')
        required = {'LICENSE', 'AGENTS.md', 'README.md', 'docs/THIRD-PARTY-NOTICES.md',
                    'scripts/Publish.ps1', 'src/Launcher.App/Assets/Brand/LauncherIcon.ico'}
        if not required.issubset(archive.namelist()):
            raise RuntimeError('Source archive is missing required files')
    temporary.replace(source)
finally:
    temporary.unlink(missing_ok=True)

assets = [output / f'NovainaLauncher-{version}-win-x64.exe',
          output / f'NovainaLauncher-{version}-win-x64-lite.exe', source]
records = []
for path in assets:
    if not path.exists():
        continue
    with path.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    records.append({'file': path.name, 'bytes': path.stat().st_size, 'sha256': digest})
(output / 'SHA256SUMS.txt').write_text(''.join(f"{item['sha256']}  {item['file']}\n" for item in records), encoding='utf-8')
(output / 'release-manifest.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
print(json.dumps({'source_files': len(files), 'assets': records}, indent=2))
