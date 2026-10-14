#!/usr/bin/env python3
"""Collect idle generated output and explicitly configured disposable CI archives.

Dry-run is the default. Source worktrees, shared caches, databases, Docker state and
VM disks are never collection roots. Configuration is private to each host.
"""
import argparse
import contextlib
import datetime as dt
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

MC_RUN = re.compile(r'^\d{8}-\d{6}-([a-f0-9]{12})$')
SK_RUN = re.compile(r'^(?:pr-(\d+)-([a-f0-9]{12})|staging-([a-f0-9]{12})-(?:fast|all|api|admin|web))-\d{8}T\d{6}Z$')
SK_WORKSPACE = re.compile(r'^pr-\d+$')
WINDOWS_WORKSPACE = re.compile(r'^v\d+-pr-\d+$')
MC_JOBS = {'linux': ('build', 'demo', 'tests', 'diffcoverage', 'properties', 'soak', 'cultures', 'aot', 'packages', 'docs'),
           'macos': ('build', 'unit'), 'windows': ('build', 'unit')}
SKIP = {'.git', 'node_modules', '.venv', 'venv', 'vendor', '.pub-cache', '.gradle', '.claude', '.codex', 'Pods'}
DATA_SUFFIXES = {'.db', '.sqlite', '.sqlite3', '.voron', '.pfx', '.p12', '.key', '.pem'}


def emit(action, path, **extra):
    print(json.dumps({'at': dt.datetime.now(dt.timezone.utc).isoformat(), 'action': action,
                      'path': str(path), **extra}), flush=True)


def plain(path):
    return not any(p.is_symlink() or (hasattr(p, 'is_junction') and p.is_junction()) for p in (path, *path.parents))


def remove_tree(path):
    if os.name == 'nt':
        import stat
        def readonly(function, name, error):
            if not isinstance(error, PermissionError) or not plain(Path(name)):
                raise error
            os.chmod(name, stat.S_IWRITE)
            function(name)
        shutil.rmtree(path, onexc=readonly)
    else:
        shutil.rmtree(path)


def git(root, *args):
    r = subprocess.run(['git', '--no-optional-locks', '-c', 'safe.directory=' + str(root), '-C', str(root), *args],
                       text=True, capture_output=True, timeout=30)
    return r.stdout.strip() if r.returncode == 0 else None


def activity():
    """Fail closed if open files cannot be inspected; ignore this collector's ancestors."""
    if os.name == 'nt':
        return windows_activity()
    ignored = {os.getpid()}
    pid = os.getppid()
    while pid > 1 and pid not in ignored:
        ignored.add(pid)
        parent = subprocess.run(['ps', '-o', 'ppid=', '-p', str(pid)], text=True,
                                capture_output=True, timeout=10).stdout.strip()
        if not parent.isdigit():
            break
        pid = int(parent)
    inspector = subprocess.Popen(['lsof', '-n', '-w', '-F', 'pn'], text=True,
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        out, _ = inspector.communicate(timeout=45)
    except subprocess.TimeoutExpired:
        inspector.kill(); inspector.communicate()
        raise RuntimeError('open-file inspection timed out')
    if not out:
        raise RuntimeError('open-file inspection unavailable')
    paths = set()
    owner = None
    for line in out.splitlines():
        if line.startswith('p') and line[1:].isdigit():
            owner = int(line[1:])
        elif line.startswith('n/') and owner not in ignored and owner != inspector.pid:
            paths.add(os.path.realpath(line[1:]))
    # Arguments catch workers between opening files and starting the next build.
    processes = subprocess.run(['ps', '-axo', 'pid=,args='], text=True, capture_output=True,
                               timeout=10, check=True).stdout
    for line in processes.splitlines():
        parts = line.strip().split(None, 1)
        if len(parts) == 2 and parts[0].isdigit() and int(parts[0]) not in ignored:
            for word in re.findall(r'/[^\s"\']+', parts[1]):
                if os.path.exists(word):
                    paths.add(os.path.realpath(word))
    return paths


def windows_activity():
    # Administrative scheduled task: WMI supplies process arguments across accounts.
    # Windows does not expose cwd here. A build tool anywhere therefore protects
    # every configured root until it exits; locked files also make rmtree fail.
    script = "$ErrorActionPreference='Stop'; Get-CimInstance Win32_Process | Select-Object ProcessId,Name,ExecutablePath,CommandLine | ConvertTo-Json -Compress"
    r = subprocess.run(['powershell.exe', '-NoProfile', '-Command', script], text=True,
                       capture_output=True, timeout=45, check=True)
    processes = json.loads(r.stdout)
    if not isinstance(processes, list):
        processes = [processes]
    result = set()
    tools = re.compile(r'^(?:dotnet|MSBuild|VBCSCompiler|dart|dartvm|flutter|node|rustc|cargo|java|ninja|cmake|cl)\.exe$', re.I)
    for p in processes:
        if p['ProcessId'] == os.getpid():
            continue
        if tools.fullmatch(p['Name']):
            result.add('*')
        if p.get('ExecutablePath'):
            result.add(os.path.realpath(p['ExecutablePath']))
        for word in re.findall(r'[A-Za-z]:[\\/][^\s"\']+', p.get('CommandLine') or ''):
            if os.path.exists(word):
                result.add(os.path.realpath(word))
    return result


def active(root, paths):
    prefix = str(root.resolve())
    return '*' in paths or any(os.path.normcase(p) == os.path.normcase(prefix) or
                              os.path.normcase(p).startswith(os.path.normcase(prefix + os.sep)) for p in paths)


def tree(path, archive=False):
    """Allocated bytes, newest modification, and local data/symlink protection."""
    size = 0
    newest = 0
    protected = False
    for base, directories, files in os.walk(path, followlinks=False):
        for name in directories[:]:
            p = Path(base, name)
            if p.is_symlink() or (hasattr(p, 'is_junction') and p.is_junction()):
                directories.remove(name)
                protected |= not archive
        for name in files:
            p = Path(base, name)
            s = p.lstat()
            size += getattr(s, 'st_blocks', 0) * 512 or s.st_size
            newest = max(newest, s.st_mtime)
            if p.is_symlink():
                protected |= not archive
                continue
            protected |= p.suffix.lower() in DATA_SUFFIXES or name == '.env' or name.startswith('.env.local')
    return size, newest or path.stat().st_mtime, protected


def clean_source(root):
    if not (root / '.git').exists() or not plain(root / '.git'):
        return False
    directory = git(root, 'rev-parse', '--absolute-git-dir')
    if directory is None or any((Path(directory) / n).exists() for n in ('index.lock', 'locked')):
        return False
    return git(root, 'status', '--porcelain=v1', '-uall') == ''


def builds(root, snapshot=False):
    tracked = [] if snapshot else (git(root, 'ls-files', '-z') or '').split('\0')
    for base, directories, files in os.walk(root, followlinks=False):
        directories[:] = [d for d in directories if d not in SKIP and plain(Path(base, d))]
        node_build = False
        if 'package.json' in files:
            try:
                node_build = bool(json.loads(Path(base, 'package.json').read_text()).get('scripts', {}).get('build'))
            except (OSError, ValueError, AttributeError):
                # A malformed project file cannot establish generated-output ownership.
                pass
        for name in directories[:]:
            p = Path(base, name)
            generated = name in ('bin', 'obj') and any(f.endswith(('.csproj', '.fsproj', '.vbproj')) for f in files)
            generated |= name == 'build' and ('pubspec.yaml' in files or 'build.gradle' in files or 'build.gradle.kts' in files)
            generated |= name == '.next' and 'package.json' in files
            generated |= name in ('dist', 'out', 'build') and node_build
            generated |= name == '.build' and 'Package.swift' in files
            generated |= name == 'target' and 'Cargo.toml' in files
            if name == '.dart_tool' and 'pubspec.yaml' in files:
                p = p / 'flutter_build'; generated = p.is_dir() and plain(p)
            if not generated:
                continue
            directories.remove(name)
            relative = p.relative_to(root).as_posix()
            if any(t == relative or t.startswith(relative + '/') for t in tracked):
                continue
            if not snapshot and subprocess.run(['git', '-c', 'safe.directory=' + str(root), '-C', str(root), 'check-ignore', '-q', '--', relative + '/'],
                                               timeout=15).returncode != 0:
                continue
            yield p


def clean_builds(root, idle_seconds, now, paths, *, apply=False, snapshot=False, recheck=False):
    if not plain(root) or active(root, paths) or (not snapshot and not clean_source(root)):
        return 0
    removed = 0
    fresh = None
    for path in builds(root, snapshot):
        size, newest, protected = tree(path)
        if protected or now - newest < idle_seconds:
            continue
        if apply:
            if fresh is None:
                fresh = activity() if recheck else paths
            if active(root, fresh) or not plain(path) or (not snapshot and not clean_source(root)):
                continue
            size2, newest2, protected2 = tree(path)
            if protected2 or newest2 != newest or size2 != size:
                continue
            remove_tree(path)
        removed += size
        emit('remove-build' if apply else 'candidate-build', path, bytes=size)
    return removed


def archive_info(path, cfg, now):
    if not plain(path) or not path.is_dir() or (os.name != 'nt' and path.stat().st_uid != os.getuid()):
        return None
    kind = cfg['kind']
    finished = path.stat().st_mtime
    passed = False
    snapshot = True
    if kind == 'methodcache':
        match = MC_RUN.fullmatch(path.name)
        if not match or not (path / '.git').is_dir() or not clean_source(path):
            return None
        sha = git(path, 'rev-parse', 'HEAD')
        origin = git(path, 'config', '--get', 'remote.origin.url')
        if not sha or not sha.startswith(match[1]) or not origin or os.path.normcase(os.path.normpath(origin)) != os.path.normcase(os.path.normpath(cfg['mirror'])):
            return None
        snapshot = False
        report = Path(cfg['reports']) / path.name / 'macos' if cfg.get('reports') else path / 'artifacts/ci'
        if not plain(report):
            return None
        if (report / 'finished').is_file() and plain(report / 'finished'):
            finished = (report / 'finished').stat().st_mtime
            exits = [report / (n + '.exit') for n in MC_JOBS[cfg.get('platform', 'macos' if cfg.get('reports') else 'linux')]]
            passed = (report / 'finished').read_text().strip() in ('', '0') and all(
                p.is_file() and plain(p) and p.read_text().strip() == '0' for p in exits)
    elif kind == 'skribera-workspace':
        if not SK_WORKSPACE.fullmatch(path.name) or not clean_source(path):
            return None
        snapshot = False
    elif kind == 'skribera-windows-workspace':
        if not WINDOWS_WORKSPACE.fullmatch(path.name) or not (path / '.ci-last-used.json').is_file() or not (path / 'Skribera.sln').is_file():
            return None
        if (path / '.git').exists():
            if not clean_source(path):
                return None
            snapshot = False
    else:
        if not SK_RUN.fullmatch(path.name):
            return None
        markers = {'skribera-source': ('Skribera.sln', 'Skribera.slnx'),
                   'skribera-media': ('result.json', 'source.iso'),
                   'skribera-upload': ('source.tar.gz',),
                   'skribera-log': ('external-ci.log',)}
        if kind not in markers or not any((path / m).is_file() and plain(path / m) for m in markers[kind]):
            return None
        if (path / '.git').exists():
            if not clean_source(path):
                return None
            snapshot = False
    metadata = path / '.ci-last-used.json'
    if metadata.exists():
        if not plain(metadata):
            return None
        try:
            record = json.loads(metadata.read_text())
            if kind == 'skribera-windows-workspace':
                run = SK_RUN.fullmatch(record.get('runId', ''))
                if not run or path.name.split('-pr-')[-1] != run[1] or str(record.get('pr')) != run[1] or (record.get('sha') or '')[:12] != run[2]:
                    return None
            elif record.get('runId') != path.name:
                return None
            match = SK_RUN.fullmatch(path.name)
            if match and match[1] and (str(record.get('pr')) != match[1] or record.get('shaPrefix') != match[2]):
                return None
            if kind == 'skribera-workspace' and (git(path, 'rev-parse', 'HEAD') or '')[:12] != record.get('shaPrefix'):
                return None
            finished = dt.datetime.fromisoformat(record['lastUsed'].replace('Z', '+00:00')).timestamp()
            passed = record.get('outcome') == 'success'
        except (ValueError, KeyError, TypeError):
            return None
    # Pre-metadata archives are given a full week instead of an unbounded lifetime.
    legacy = kind != 'methodcache' and not metadata.exists()
    return {'finished': finished, 'passed': passed, 'snapshot': snapshot, 'legacy': legacy}


def preserve_logs(path, retained):
    target = retained / path.name
    if not plain(target):
        raise RuntimeError('symlinked retained log destination')
    for parent in (path, path / 'artifacts/ci', path / 'ci-results', path / 'media'):
        if not parent.is_dir() or not plain(parent):
            continue
        for p in parent.rglob('*') if parent != path else parent.iterdir():
            if p.is_file() and plain(p) and (p.suffix in ('.log', '.exit', '.trx') or p.name in ('result.json', '.ci-last-used.json', 'finished')):
                destination = target / p.relative_to(path)
                destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                shutil.copy2(p, destination)
    target.mkdir(parents=True, exist_ok=True, mode=0o700)
    (target / 'collected-at').write_text(str(time.time()))


@contextlib.contextmanager
def leases(paths):
    held = []
    try:
        for name in sorted(paths):
            p = Path(name)
            if not plain(p):
                raise RuntimeError('symlinked lease')
            p.parent.mkdir(parents=True, exist_ok=True)
            f = p.open('a'); held.append(f)
            if os.name == 'nt':
                import msvcrt
                if p.stat().st_size == 0:
                    f.write('0'); f.flush()
                f.seek(0)
                try:
                    msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
                except OSError as e:
                    raise BlockingIOError('active Windows collection lease') from e
            else:
                import fcntl
                fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        for f in held:
            f.close()


def repositories(root):
    if not root.is_dir() or not plain(root):
        return
    for base, directories, _ in os.walk(root, followlinks=False):
        p = Path(base)
        directories[:] = [n for n in directories if n not in SKIP and plain(p / n)]
        if (p / '.git').exists():
            yield p
            directories[:] = []
        elif len(p.relative_to(root).parts) >= 5:
            directories[:] = []


def collect_archives(cfg, config, now, paths, apply, deadline):
    root = Path(cfg['root'])
    if not plain(root) or not root.is_dir():
        return
    with leases(cfg.get('leases', [])):
        entries = []
        for p in sorted(root.iterdir(), key=lambda p: p.stat().st_mtime if not p.is_symlink() else now):
            if time.monotonic() >= deadline:
                emit('time-budget', root); break
            info = archive_info(p, cfg, now)
            if info is None or active(p, paths):
                continue
            age = now - info['finished']
            if age < (600 if info['passed'] else config.get('failed_hours', 24) * 3600):
                continue
            output_bytes = clean_builds(p, 600 if info['passed'] else 0, now, paths, apply=apply,
                                       snapshot=info['snapshot'], recheck=apply)
            # rmtree removes internal links themselves, never their targets. CI
            # media commonly link to shared ISO files which must remain intact.
            size, newest, protected = tree(p, archive=True)
            if not apply:
                size = max(0, size - output_bytes)
            entries.append((p, info, size, newest, protected))
        total = sum(e[2] for e in entries)
        budget = cfg.get('max_gib', config.get('max_ci_gib', 10)) * 1024 ** 3
        for p, info, size, newest, protected in entries:
            if time.monotonic() >= deadline:
                break
            lifetime = 7 * 86400 if info['legacy'] else config.get('ci_keep_hours', 72) * 3600
            if not info['passed'] and not info['legacy']:
                lifetime = config.get('failed_hours', 24) * 3600
            if protected:
                emit('protected-data', p, bytes=size)
                continue
            if now - max(info['finished'], newest) < lifetime and total <= budget:
                continue
            if apply:
                if active(p, activity()) or not plain(p) or archive_info(p, cfg, now) != info:
                    continue
                fresh_size, fresh_newest, fresh_protected = tree(p, archive=True)
                if fresh_protected or fresh_newest != newest or fresh_size != size:
                    continue
                preserve_logs(p, Path(cfg['retained']))
                remove_tree(p)
            emit('remove-checkout' if apply else 'candidate-checkout', p, bytes=size)
            total -= size
        emit('archive-summary', root, remainingEligibleBytes=total, budgetBytes=budget)


def collect_xcode(root, config, now, paths, apply, deadline):
    """Known generated Xcode output only; never simulator/device data or archives."""
    if not root.is_dir() or not plain(root):
        return
    for p in root.iterdir():
        if time.monotonic() >= deadline:
            break
        if not plain(p) or not p.is_dir() or not (p / 'info.plist').is_file() or active(p, paths):
            continue
        fresh = None
        for name in ('Build', 'Index.noindex', 'Logs'):
            path = p / name
            if not path.is_dir() or not plain(path):
                continue
            size, newest, protected = tree(path)
            # Index stores are databases generated by Xcode, not application data.
            if (protected and name != 'Index.noindex') or now - newest < config.get('xcode_idle_days', 7) * 86400:
                continue
            if apply:
                if fresh is None:
                    fresh = activity()
                if active(p, fresh) or not plain(path) or tree(path) != (size, newest, protected):
                    continue
                remove_tree(path)
            emit('remove-xcode-build' if apply else 'candidate-xcode-build', path, bytes=size)


def run(config, apply):
    now = time.time()
    deadline = time.monotonic() + config.get('max_seconds', 600)
    paths = activity()
    errors = 0
    seen = set()
    jobs = [('archive', c) for c in config.get('archives', [])]
    jobs += [('xcode', p) for p in config.get('xcode_roots', [])]
    jobs += [('build', p) for p in config.get('build_roots', [])]
    start = config.get('_start', 0) % max(1, len(jobs))
    # Rotate the first root so bounded scans cannot starve later roots.
    config['_next_start'] = (start + 1) % max(1, len(jobs))
    for offset in range(len(jobs)):
        kind, cfg = jobs[(start + offset) % len(jobs)]
        root = cfg['root'] if kind == 'archive' else cfg
        if time.monotonic() >= deadline:
            emit('time-budget', root)
            break
        try:
            if kind == 'archive':
                collect_archives(cfg, config, now, paths, apply, deadline)
            elif kind == 'xcode':
                collect_xcode(Path(cfg), config, now, paths, apply, deadline)
            else:
                for p in repositories(Path(cfg)):
                    if p in seen:
                        continue
                    seen.add(p)
                    if time.monotonic() >= deadline:
                        emit('time-budget', p)
                        break
                    clean_builds(p, config.get('build_idle_hours', 24) * 3600, now, paths, apply=apply, recheck=apply)
        except BlockingIOError:
            emit('protected-lease', root)
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as e:
            errors += 1
            emit('error', root, error=str(e))
    if apply:
        # Only directories previously written by this collector can expire as retained logs.
        for root in {Path(c['retained']) for c in config.get('archives', [])}:
            if not plain(root) or not root.is_dir():
                continue
            for p in root.iterdir():
                marker = p / 'collected-at'
                if plain(p) and plain(marker) and marker.is_file() and now - marker.stat().st_mtime >= config.get('log_keep_days', 14) * 86400 and not active(p, paths):
                    remove_tree(p)
                    emit('remove-retained-logs', p)
    if errors:
        raise RuntimeError('collection errors occurred; see the collector log')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    scope = parser.add_mutually_exclusive_group(required=True)
    scope.add_argument('--config', type=Path)
    scope.add_argument('--completed-build', type=Path, help='generated output only after this successful local CI runner exits')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if args.completed_build:
        root = args.completed_build.resolve()
        clean_builds(root, 0, time.time(), activity(), apply=args.apply, recheck=args.apply)
        return
    config = json.loads(args.config.read_text())
    with leases([str(args.config.with_suffix('.lock'))]):
        progress = args.config.with_suffix('.progress.json')
        if not plain(progress):
            raise RuntimeError('symlinked scan progress refused')
        try:
            config['_start'] = int(json.loads(progress.read_text())['next'])
        except FileNotFoundError:
            config['_start'] = 0
        try:
            run(config, args.apply)
        finally:
            if args.apply and '_next_start' in config:
                fd, name = tempfile.mkstemp(dir=progress.parent, prefix='.storage-progress-')
                try:
                    with os.fdopen(fd, 'w') as stream:
                        json.dump({'next': config['_next_start']}, stream)
                    os.replace(name, progress)
                finally:
                    if os.path.exists(name):
                        os.unlink(name)


if __name__ == '__main__':
    try:
        main()
    except BlockingIOError:
        print('collector already running', file=sys.stderr)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print('storage collection failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
