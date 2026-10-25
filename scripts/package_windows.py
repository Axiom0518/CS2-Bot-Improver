"""Build a versioned Windows overlay; optionally back up and deploy its files."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import urllib.request
import zipfile
from datetime import datetime

ROOT = Path(__file__).resolve().parents[1]
PLUGINS = ('BotAI', 'BotAimImprover', 'BotBuy', 'BotControllerImpl',
           'BotHiderImpl', 'BotRandomizer', 'BotState', 'NadeSystem', 'RoundDamageRecap')


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def put(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)


def game_closed():
    listing = subprocess.check_output(['tasklist', '/FI', 'IMAGENAME eq cs2.exe', '/FO', 'CSV'], text=True)
    if '"cs2.exe"' in listing.lower():
        raise RuntimeError('Close CS2 before deploying files.')


def build(output):
    runtime = json.loads((ROOT / 'deployment/windows-runtime.json').read_text(encoding='utf-8'))
    cache = ROOT / 'artifacts/downloads'
    cache.mkdir(parents=True, exist_ok=True)
    stage = output / 'csgo'
    stage.mkdir(parents=True)
    for name, release in runtime.items():
        archive = cache / release['url'].rsplit('/', 1)[1]
        if not archive.is_file() or sha(archive) != release['sha256']:
            print('Downloading', name, release['version'], flush=True)
            request = urllib.request.Request(release['url'], headers={'User-Agent': 'CS2-Bot-Improver-packager'})
            archive.write_bytes(urllib.request.urlopen(request, timeout=120).read())
        if sha(archive) != release['sha256']:
            raise RuntimeError('Release SHA256 mismatch: ' + name)
        with zipfile.ZipFile(archive) as package:
            for info in package.infolist():
                if info.is_dir():
                    continue
                relative = Path(info.filename)
                # Framework code and native binaries only. Configurations and
                # managed plugin implementations come from this local project.
                if name == 'CounterStrikeSharp':
                    selected = any(info.filename.startswith('addons/counterstrikesharp/' + part + '/')
                                   for part in ('api', 'bin/win64', 'lang'))
                elif name == 'Metamod':
                    selected = info.filename in ('addons/metamod/bin/win64/server.dll',
                                                'addons/metamod/bin/win64/metamod.2.cs2.dll')
                else:
                    selected = info.filename.startswith('addons/' + name + '/bin/win64/')
                if selected:
                    destination = (stage / relative).resolve()
                    if not destination.is_relative_to(stage.resolve()):
                        raise RuntimeError('Unsafe release path: ' + info.filename)
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    destination.write_bytes(package.read(info))

    tracked = subprocess.check_output(['git', 'ls-files', '-z', 'addons', 'cfg', 'overrides'], cwd=ROOT).decode().split('\0')
    for relative in tracked:
        path = Path(relative)
        if not relative or path.suffix not in ('.json', '.cfg', '.vpk', '.kv3', '.vdf'):
            continue
        if '/disabled/' in relative or '/tools/' in relative or '/libs/' in relative or '.linux.' in relative:
            continue
        put(ROOT / path, stage / path)
        # This fork reads lineup JSON from ModuleDirectory/grenades.
        prefix = 'addons/counterstrikesharp/data/NadeSystem/grenades/'
        if relative.startswith(prefix):
            put(ROOT / path, stage / 'addons/counterstrikesharp/plugins/NadeSystem/grenades' / path.name)

    build_root = output / 'build'
    projects = [('shared', name) for name in ('BotControllerApi', 'BotHiderApi')]
    projects += [('plugins', name) for name in PLUGINS]
    for category, name in projects:
        project = ROOT / 'addons/counterstrikesharp' / category / name / (name + '.csproj')
        destination = build_root / name
        print('Building', name, flush=True)
        result = subprocess.run(['dotnet', 'build', str(project), '-c', 'Release', '-o', str(destination),
                                 '--nologo', '--verbosity', 'quiet'], cwd=ROOT, capture_output=True,
                                text=True, encoding='utf-8', errors='replace')
        (output / (name + '-build.log')).write_text(result.stdout + result.stderr, encoding='utf-8')
        if result.returncode:
            raise RuntimeError('Build failed; see ' + str(output / (name + '-build.log')))
        target = stage / 'addons/counterstrikesharp' / category / name
        for suffix in ('.dll', '.pdb', '.deps.json'):
            put(destination / (name + suffix), target / (name + suffix))
        if name == 'NadeSystem':
            put(destination / 'RayTraceApi.dll', target / 'RayTraceApi.dll')
        if name == 'BotHiderImpl':
            put(destination / 'shared/0Harmony/0Harmony.dll', stage / 'addons/counterstrikesharp/shared/0Harmony/0Harmony.dll')

    files = {p.relative_to(stage).as_posix(): sha(p) for p in sorted(stage.rglob('*')) if p.is_file()}
    manifest = {'created': datetime.now().astimezone().isoformat(),
                'sourceCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                'includesUncommittedSource': bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT).strip()),
                'runtime': runtime, 'files': files}
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    zip_path = output / 'CS2-Bot-Improver-Windows.zip'
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED) as package:
        for relative in files:
            package.write(stage / relative, relative)
        package.write(output / 'manifest.json', 'deployment-manifest.json')
    print('Package:', zip_path, 'Files:', len(files), 'SHA256:', sha(zip_path), flush=True)
    return stage, manifest


def deploy(stage, manifest, game_root, output):
    game_root = game_root.resolve(strict=True)
    if game_root.name.lower() != 'csgo' or not (game_root / 'steam.inf').is_file():
        raise RuntimeError('Expected the installed game/csgo directory.')
    game_closed()
    backup = output / 'backup'
    backup.mkdir()
    entries = []
    # Complete all backups before the first game-file write.
    for relative, digest in manifest['files'].items():
        target = (game_root / relative).resolve()
        if not target.is_relative_to(game_root):
            raise RuntimeError('Unsafe deployment path: ' + relative)
        if sha(stage / relative) != digest:
            raise RuntimeError('Package changed: ' + relative)
        old = sha(target) if target.is_file() else None
        if old == digest:
            continue
        if old is not None:
            put(target, backup / relative)
            if sha(backup / relative) != old:
                raise RuntimeError('Backup verification failed: ' + relative)
        entries.append({'path': relative, 'before': old, 'after': digest})
    report = {'gameRoot': str(game_root), 'backup': str(backup), 'files': entries, 'status': 'backed-up'}
    report_path = output / 'deployment.json'
    report_path.write_text(json.dumps(report, indent=2), encoding='utf-8')
    game_closed()
    for entry in entries:
        relative = entry['path']
        target = game_root / relative
        old = sha(target) if target.is_file() else None
        if old != entry['before']:
            raise RuntimeError('Game file changed after backup: ' + relative)
        put(stage / relative, target)
        if sha(target) != entry['after']:
            raise RuntimeError('Deployment verification failed: ' + relative)
    for relative, digest in manifest['files'].items():
        if sha(game_root / relative) != digest:
            raise RuntimeError('Final verification failed: ' + relative)
    report['status'] = 'verified'
    report_path.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('Deployed and verified:', len(entries), 'changed files; backup:', backup, flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--deploy', type=Path, help='Installed game/csgo directory; CS2 must be closed')
    args = parser.parse_args()
    output = ROOT / 'artifacts' / ('windows-' + datetime.now().strftime('%Y%m%d-%H%M%S'))
    stage, manifest = build(output)
    if args.deploy:
        deploy(stage, manifest, args.deploy, output)
