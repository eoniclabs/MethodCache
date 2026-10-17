"""Recognize competing build/test work without recording process arguments."""
import re
from typing import NamedTuple


class Process(NamedTuple):
    pid: int
    parent: int
    command: str
    state: str = ''
    cpu_percent: float | None = None
    age_seconds: float = 0
    cpu_seconds: float | None = None


def duration_seconds(value):
    days, separator, clock = value.partition('-')
    if not separator:
        clock, days = days, '0'
    total = 0.0
    for part in clock.split(':'):
        total = total * 60 + float(part.replace(',', '.'))
    return int(days) * 86400 + total


def parse_snapshot(output):
    processes = []
    for line in output.splitlines():
        fields = line.strip().split(None, 6)
        if len(fields) != 7:
            continue
        try:
            pid, parent, state, cpu, elapsed_cpu, age, command = fields
            processes.append(Process(int(pid), int(parent), command, state, float(cpu.replace(',', '.')),
                                     duration_seconds(age), duration_seconds(elapsed_cpu)))
        except ValueError:
            continue
    return processes


def parse_processes(output):
    processes = []
    for line in output.splitlines():
        fields = line.strip().split(None, 2)
        if len(fields) != 3:
            continue
        try:
            processes.append((int(fields[0]), int(fields[1]), fields[2]))
        except ValueError:
            continue  # A newline inside process arguments is not another process row.
    return processes


def external_activity(processes, owned_pid=None, previous_cpu_times=None):
    processes = [Process(*row) for row in processes]
    owned = {owned_pid} if owned_pid else set()
    while True:
        children = {process.pid for process in processes if process.parent in owned}
        if children.issubset(owned):
            break
        owned.update(children)
    result = []
    for process in processes:
        pid, parent, command = process[:3]
        if pid in owned:
            continue
        command = command.lower()
        patterns = ('testhost.dll', 'dotnet build ', 'dotnet test ', 'dotnet publish ',
                    'flutter_tester', 'vitest', 'jest.js', 'jest/bin', 'xcodebuild',
                    ' -m unittest', ' -m pytest', 'pytest ', 'npm test', 'pnpm test',
                    'npm run build', 'pnpm build', 'vite build', 'next build',
                    'swift test', 'swift build', 'cargo test', 'cargo build', 'go test ')
        flutter = 'flutter_tools.snapshot' in command and re.search(
            r'(?:^|\s)(test|build|analyze|drive)(?:\s|$)', command.split('flutter_tools.snapshot', 1)[1])
        if flutter or any(pattern in command for pattern in patterns):
            # Old sleeping orphan workers can remain after their test runner has gone. Do not wait forever for
            # these inert processes, but still notice an increase in their CPU work even if lifetime %CPU rounds to zero.
            previous = (previous_cpu_times or {}).get(pid)
            advancing = previous is not None and process.cpu_seconds is not None and process.cpu_seconds - previous > 0.05
            dormant = (parent == 1 and process.age_seconds >= 3600 and process.state.startswith('S')
                       and process.cpu_percent == 0 and not advancing)
            if dormant:
                continue
            result.append({'pid': pid, 'kind': 'build/test/publish'})
    return result


class ActivityMonitor:
    def __init__(self):
        self.previous_cpu_times = {}

    def observe(self, snapshot, owned_pid=None):
        processes = parse_snapshot(snapshot)
        active = external_activity(processes, owned_pid, self.previous_cpu_times)
        self.previous_cpu_times = {process.pid: process.cpu_seconds for process in processes}
        return active
