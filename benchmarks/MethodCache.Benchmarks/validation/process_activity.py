"""Recognize competing build/test work without recording process arguments."""
import re


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


def external_activity(processes, owned_pid=None):
    owned = {owned_pid} if owned_pid else set()
    while True:
        children = {pid for pid, parent, _ in processes if parent in owned}
        if children.issubset(owned):
            break
        owned.update(children)
    result = []
    for pid, _, command in processes:
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
            result.append({'pid': pid, 'kind': 'build/test/publish'})
    return result
