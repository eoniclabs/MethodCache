#!/usr/bin/env python3
"""Run a reviewed command while holding the configured POSIX host capacity leases.

Uses lockf (not flock): compatible with macOS's existing Skribera coordinator.
Installed outside checkouts; all admitted projects use the same wrapper and locks.
"""
import argparse
import fcntl
import os
from pathlib import Path
import signal
import subprocess
import sys
import time


def group_alive(child):
    child.poll()
    try:
        os.killpg(child.pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        # Darwin reports EPERM for groups containing only reparented zombies.
        rows = subprocess.check_output(['/bin/ps', '-axo', 'pgid=,stat='], text=True)
        live = [row for row in rows.splitlines() if row.split()[0] == str(child.pid)
                and not row.split()[1].startswith('Z')]
        # A dying member may still appear runnable briefly: keep the lease and poll again.
        return bool(live)


def terminate_group(child, seconds):
    if group_alive(child):
        try:
            os.killpg(child.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        deadline = time.monotonic() + seconds
        while group_alive(child) and time.monotonic() < deadline:
            time.sleep(0.1)
        if group_alive(child):
            try:
                os.killpg(child.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
    child.wait()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--lock', action='append', default=[])
    parser.add_argument('--wait-seconds', type=float, default=1800)
    parser.add_argument('--run-seconds', type=float, default=3600)
    parser.add_argument('--kill-seconds', type=float, default=30)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    locks = args.lock or os.environ.get('EONIC_CI_CAPACITY_LOCKS', '').split(os.pathsep)
    if not command or not all(locks):
        parser.error('A command and at least one capacity lock are required')
    leases = []
    child = None
    interrupted = []
    def interrupt(number, _frame):
        interrupted.append(number)
    for number in (signal.SIGINT, signal.SIGTERM):
        signal.signal(number, interrupt)
    try:
        deadline = time.monotonic() + args.wait_seconds
        # A fixed order prevents competing multi-lock clients deadlocking.
        for name in sorted(set(locks)):
            path = Path(name).expanduser()
            path.parent.mkdir(parents=True, exist_ok=True)
            lease = path.open('a+b')
            leases.append(lease)
            print(f'Waiting for host capacity: {path}', flush=True)
            while not interrupted:
                try:
                    fcntl.lockf(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    break
                except BlockingIOError:
                    if time.monotonic() >= deadline:
                        return 75
                    time.sleep(0.1)
            if interrupted:
                return 128 + interrupted[0]
        child = subprocess.Popen(command, start_new_session=True)
        deadline = time.monotonic() + args.run_seconds
        while child.poll() is None and not interrupted and time.monotonic() < deadline:
            time.sleep(0.1)
        if child.poll() is None:
            code = 128 + interrupted[0] if interrupted else 124
        else:
            code = child.returncode if child.returncode >= 0 else 128 - child.returncode
        # Also retire background children after success; they still consume host capacity.
        terminate_group(child, args.kill_seconds)
        return code
    finally:
        for lease in reversed(leases):
            lease.close()


if __name__ == '__main__':
    sys.exit(main())
