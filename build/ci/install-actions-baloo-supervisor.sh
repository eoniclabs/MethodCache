#!/usr/bin/env bash
# Limited Windows account: supervise its runner over existing SSH from the logged-in CI Mac.
set -euo pipefail
label=com.eoniclabs.actions.baloo
plist="$HOME/Library/LaunchAgents/$label.plist"
[ ! -e "$plist" ] || { echo 'Supervisor already installed; inspect before changing' >&2; exit 1; }
mkdir -p "$HOME/Library/LaunchAgents" "$HOME/Library/Logs"
python3 - "$plist" "$HOME" "$label" <<'PY'
import plistlib, sys
path, home, label = sys.argv[1:]
with open(path, 'wb') as output:
    plistlib.dump({
        'Label': label,
        'ProgramArguments': ['/usr/bin/ssh', '-T', '-o', 'BatchMode=yes',
            '-o', 'ConnectTimeout=15', '-o', 'ServerAliveInterval=30', '-o', 'ServerAliveCountMax=3',
            'bldr@baloo', r'powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\Users\bldr\actions-runner\start-runner.ps1'],
        'RunAtLoad': True, 'KeepAlive': True, 'ThrottleInterval': 60,
        'ProcessType': 'Background',
        'StandardOutPath': home + '/Library/Logs/actions-baloo.log',
        'StandardErrorPath': home + '/Library/Logs/actions-baloo.err.log',
    }, output)
PY
launchctl bootstrap "gui/$(id -u)" "$plist"
echo "Installed $label; runner depends on Megatron's logged-in LaunchAgent and SSH reachability."
