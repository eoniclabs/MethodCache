#!/usr/bin/env python3
"""Validate/reuse complete CI results and collect owned, idle macOS checkouts."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

JOBS = {
    'linux': ('build', 'demo', 'tests', 'diffcoverage', 'properties', 'soak', 'cultures', 'aot', 'packages', 'docs'),
    'windows': ('build', 'unit'),
    'macos': ('build', 'unit'),
}
RUN = re.compile(r'^\d{8}-\d{6}-([a-f0-9]{12})$')
SHA = re.compile(r'^[a-f0-9]{40}$')


def complete(directory, passed=False):
    directory = Path(directory)
    jobs = JOBS.get(directory.name)
    if not jobs or directory.is_symlink() or not (directory / 'finished').is_file() or (directory / 'finished').is_symlink():
        return False
    try:
        terminal = (directory / 'finished').read_text().strip()
    except OSError:
        return False
    if terminal and not re.fullmatch(r'\d+', terminal):
        return False
    if passed and terminal not in ('', '0'):
        return False
    for job in jobs:
        if (directory / (job + '.exit')).is_symlink():
            return False
        try:
            code = (directory / (job + '.exit')).read_text().strip()
        except OSError:
            return False
        if not (code == 'skipped' or re.fullmatch(r'\d+', code)) or (passed and code != '0'):
            return False
    return True


def git(checkout, *args):
    result = subprocess.run(['git', '--no-optional-locks', '-C', str(checkout), *args],
                            text=True, capture_output=True, timeout=15)
    return result.stdout.strip() if result.returncode == 0 else None


def checkout_sha(state, run):
    checkout = state / 'macos' / 'runs' / run
    if checkout.is_symlink() or not (checkout / '.git').is_dir() or (checkout / '.git').is_symlink():
        return None
    return git(checkout, 'rev-parse', 'HEAD')


def result_sha(state, directory):
    if (directory / 'commit').is_symlink():
        return None
    try:
        sha = (directory / 'commit').read_text().strip()
    except OSError:
        # Adopt old reports only when their full SHA can still be proven from the corresponding local clone.
        sha = checkout_sha(state, directory.name)
    match = RUN.fullmatch(directory.name)
    return sha if match and sha and SHA.fullmatch(sha) and sha.startswith(match[1]) else None


def previous(state, sha, exclude=None):
    reports = state / 'runs'
    if reports.is_symlink() or not reports.is_dir():
        return []
    return [p for p in sorted(reports.iterdir(), key=lambda p: p.name, reverse=True)
            if p.is_dir() and not p.is_symlink() and p.name != exclude and result_sha(state, p) == sha]


def reuse(state, sha, platform, destination):
    destination = Path(destination)
    if (state / 'runs').is_symlink() or destination.is_symlink() or destination.parent.is_symlink():
        raise ValueError('symlinked result directory refused')
    if platform not in JOBS or destination.name != platform or destination.parent.parent.resolve() != (state / 'runs').resolve():
        raise ValueError('result destination must be a direct platform directory under state/runs')
    if not SHA.fullmatch(sha):
        raise ValueError('invalid commit SHA')
    for report in previous(state, sha, destination.parent.name):
        source = report / platform
        if source.is_symlink() or not complete(source, passed=True):
            continue
        destination.mkdir(parents=True, exist_ok=True)
        for p in source.iterdir():
            if p.is_file() and not p.is_symlink() and (p.suffix in ('.exit', '.log') or p.name == 'finished'):
                shutil.copy2(p, destination / p.name)
        (destination / 'reused-from').write_text(report.name + '\n')
        return True
    return False


def open_paths():
    result = subprocess.run(['lsof', '-n', '-w', '-F', 'n'], text=True, capture_output=True, timeout=30)
    if not result.stdout:
        raise RuntimeError('cannot inspect open files; refusing checkout collection')
    return {os.path.realpath(line[1:]) for line in result.stdout.splitlines() if line.startswith('n/')}


def used(checkout, paths):
    root = str(checkout)
    return any(p == root or p.startswith(root + '/') for p in paths)


def size_bytes(directory):
    # du reports allocated blocks; this remains a conservative estimate when APFS shares blocks.
    result = subprocess.run(['du', '-sk', str(directory)], text=True, capture_output=True, timeout=30, check=True)
    return int(result.stdout.split()[0]) * 1024


def build_directories(checkout):
    tracked = git(checkout, 'ls-files', '-z')
    if tracked is None:
        return [], True
    tracked = tracked.split('\0')
    result = []
    blocked = False
    for root, directories, files in os.walk(checkout, followlinks=False):
        directories[:] = [d for d in directories if d not in ('.git', 'artifacts') and not Path(root, d).is_symlink()]
        if not any(f.endswith(('.csproj', '.fsproj', '.vbproj')) for f in files):
            continue
        for name in ('bin', 'obj'):
            if name not in directories:
                continue
            path = Path(root, name)
            relative = path.relative_to(checkout).as_posix()
            directories.remove(name)
            if any(p == relative or p.startswith(relative + '/') for p in tracked):
                continue
            # Keep unexpected local data even in a generated directory.
            protected = any(p.is_file() and (p.suffix.lower() in ('.db', '.sqlite', '.sqlite3', '.voron', '.pfx', '.p12')
                                             or p.name.startswith('.env')) for p in path.rglob('*'))
            if protected:
                blocked = True
            else:
                result.append(path)
    return result, blocked


def collect(state, keep_days, keep_failed_hours, max_mib, dry_run=False, now=None, paths=None, only_run=None):
    now = time.time() if now is None else now
    paths = open_paths() if paths is None else paths
    root = state / 'macos' / 'runs'
    if (state / 'macos').is_symlink() or (state / 'runs').is_symlink() or root.is_symlink() or not root.is_dir():
        return []
    entries = []
    actions = []
    for checkout in sorted(root.iterdir()):
        if only_run and checkout.name != only_run:
            continue
        match = RUN.fullmatch(checkout.name)
        if not match or checkout.is_symlink() or not checkout.is_dir() or checkout.stat().st_uid != os.getuid():
            continue
        report = state / 'runs' / checkout.name
        sha = checkout_sha(state, checkout.name)
        # A plain local clone from the coordinator's mirror is the ownership boundary.
        origin = git(checkout, 'config', '--get', 'remote.origin.url')
        if not sha or not sha.startswith(match[1]) or origin != str(state / 'macos' / 'mirror.git'):
            continue
        if (checkout / '.git' / 'index.lock').exists() or used(checkout, paths) or git(checkout, 'diff', '--name-only', 'HEAD') != '':
            continue
        if report.is_symlink() or (report / 'commit').is_symlink() or not complete(report / 'macos'):
            continue
        if (report / 'commit').exists() and result_sha(state, report) != sha:
            continue
        if not dry_run:
            (report / 'commit').write_text(sha + '\n')
        passed = complete(report / 'macos', passed=True)
        builds, protected = build_directories(checkout)
        untracked = git(checkout, 'ls-files', '--others', '--exclude-standard')
        if protected or any(checkout.glob('.env*')) or untracked is None or untracked:
            continue
        removed_bytes = 0
        if passed:
            for path in builds:
                actions.append({'action': 'remove-build', 'path': str(path)})
                removed_bytes += size_bytes(path)
                if not dry_run:
                    shutil.rmtree(path)
        last_used = (report / 'macos' / 'finished').stat().st_mtime
        entries.append({'path': checkout, 'passed': passed, 'age': now - last_used,
                        'bytes': max(0, size_bytes(checkout) - (removed_bytes if dry_run else 0))})
    total = sum(e['bytes'] for e in entries)
    budget = max_mib * 1024 * 1024
    for entry in sorted(entries, key=lambda e: e['age'], reverse=True):
        # Failed runs get their diagnostic window even if this temporarily exceeds the budget.
        if not entry['passed'] and entry['age'] < keep_failed_hours * 3600:
            continue
        expired = entry['age'] >= (keep_days * 86400 if entry['passed'] else keep_failed_hours * 3600)
        if expired or total > budget:
            if not dry_run:
                if used(entry['path'], open_paths()):
                    actions.append({'action': 'protected-active', 'path': str(entry['path'])})
                    continue
                shutil.rmtree(entry['path'])
            actions.append({'action': 'remove-checkout', 'path': str(entry['path'])})
            total -= entry['bytes']
    if total > budget:
        actions.append({'action': 'protected-budget-excess', 'bytes': total - budget})
    return actions


def schedule_retry(state, sha, limit, now=None, reset=False):
    now = time.time() if now is None else now
    if not SHA.fullmatch(sha) or limit < 1:
        raise ValueError('invalid retry SHA or attempt limit')
    path = state / 'retries' / (sha + '.json')
    try:
        old = json.loads(path.read_text()) if not reset else {}
    except FileNotFoundError:
        old = {}
    attempts = old.get('attempts', 0) + 1
    record = {'attempts': attempts, 'limit': limit,
              'next_at': now + min(30 * 60 * 2 ** min(attempts - 1, 10), 6 * 3600)}
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(record))
    temporary.replace(path)
    return record


def retry_due(state, sha, now=None):
    now = time.time() if now is None else now
    if not SHA.fullmatch(sha):
        raise ValueError('invalid retry SHA')
    path = state / 'retries' / (sha + '.json')
    try:
        record = json.loads(path.read_text())
    except FileNotFoundError:
        # Preserve the waiting period on retry markers created by the older coordinator.
        return now - (state / 'done' / sha).stat().st_mtime >= 30 * 60
    return record['attempts'] < record['limit'] and now >= record['next_at']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    for name in ('complete', 'passed'):
        c = commands.add_parser(name); c.add_argument('directory')
    for name in ('reuse', 'previous', 'gc', 'retry', 'retry-due'):
        p = commands.add_parser(name); p.add_argument('--state', type=Path, required=True)
        if name in ('reuse', 'previous', 'retry', 'retry-due'):
            p.add_argument('--sha', required=True)
        if name == 'retry':
            p.add_argument('--limit', type=int, default=5); p.add_argument('--reset', action='store_true')
        if name == 'reuse':
            p.add_argument('--platform', choices=JOBS, required=True); p.add_argument('--destination', type=Path, required=True)
        if name == 'previous':
            p.add_argument('--exclude-run')
        if name == 'gc':
            p.add_argument('--keep-days', type=int, default=3); p.add_argument('--keep-failed-hours', type=int, default=24)
            p.add_argument('--max-mib', type=int, default=10240); p.add_argument('--dry-run', action='store_true')
            p.add_argument('--only-run'); p.add_argument('--min-interval-seconds', type=int, default=0)
    args = parser.parse_args()
    if args.command in ('complete', 'passed'):
        return 0 if complete(args.directory, passed=args.command == 'passed') else 1
    state = args.state.resolve()
    if args.command == 'retry':
        record = schedule_retry(state, args.sha, args.limit, reset=args.reset)
        print(json.dumps(record))
        if record['attempts'] >= record['limit']:
            print('Automatic retries exhausted for ' + args.sha + '; use --pr for a deliberate new run.', file=sys.stderr)
        return 0
    if args.command == 'retry-due':
        return 0 if retry_due(state, args.sha) else 1
    if args.command == 'reuse':
        return 0 if reuse(state, args.sha, args.platform, args.destination) else 1
    if args.command == 'previous':
        for p in previous(state, args.sha, args.exclude_run)[:3]:
            print(p.name)
    else:
        if min(args.keep_days, args.keep_failed_hours, args.max_mib) < 1:
            raise ValueError('retention and budget must be positive')
        if args.only_run and not RUN.fullmatch(args.only_run):
            raise ValueError('invalid checkout run ID')
        marker = state / 'last-macos-collection'
        if marker.is_symlink():
            raise ValueError('symlinked collection marker refused')
        if not args.only_run and not args.dry_run and marker.exists() and time.time() - marker.stat().st_mtime < args.min_interval_seconds:
            return 0
        for action in collect(state, args.keep_days, args.keep_failed_hours, args.max_mib, args.dry_run, only_run=args.only_run):
            print(json.dumps(action))
        if not args.only_run and not args.dry_run:
            marker.touch()
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print('coordinator state error: ' + str(error), file=sys.stderr)
        sys.exit(2)
