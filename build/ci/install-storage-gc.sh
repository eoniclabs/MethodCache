#!/usr/bin/env bash
# Install a per-host collector independent of either project's CI coordinator.
# Usage: install-storage-gc.sh /absolute/path/config.json
set -euo pipefail
config=${1:?Pass a host-specific collector configuration JSON file}
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_dir="$HOME/.local/share/build-storage-gc"
mkdir -p "$install_dir"
chmod 700 "$install_dir"
python=$(command -v python3)
"$python" -c 'import json,sys; json.load(open(sys.argv[1]))' "$config"
install -m 700 "$here/storage_gc.py" "$install_dir/storage_gc.py.new"
mv "$install_dir/storage_gc.py.new" "$install_dir/storage_gc.py"
install -m 600 "$config" "$install_dir/config.json.new"
mv "$install_dir/config.json.new" "$install_dir/config.json"
cat > "$install_dir/run.sh" <<SCRIPT
#!/bin/bash
set -euo pipefail
export PATH=/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:\$PATH
log='$install_dir/collector.log'
if [ -f "\$log" ] && [ "\$(wc -c < "\$log")" -gt 10485760 ]; then
  rm -f "\$log.5"
  for n in 4 3 2 1; do [ ! -f "\$log.\$n" ] || mv "\$log.\$n" "\$log.\$((n+1))"; done
  mv "\$log" "\$log.1"
fi
exec '$python' '$install_dir/storage_gc.py' --config '$install_dir/config.json' --apply >> "\$log" 2>&1
SCRIPT
chmod 700 "$install_dir/run.sh"
if [ "$(uname -s)" = Darwin ]; then
  label=com.eoniclabs.build-storage-gc
  plist="$HOME/Library/LaunchAgents/$label.plist"
  mkdir -p "$(dirname "$plist")"
  cat > "$plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Label</key><string>$label</string>
<key>ProgramArguments</key><array><string>$install_dir/run.sh</string></array>
<key>StartInterval</key><integer>900</integer>
<key>RunAtLoad</key><true/>
<key>ProcessType</key><string>Background</string>
</dict></plist>
PLIST
  launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || true
  launchctl bootstrap "gui/$(id -u)" "$plist"
else
  user=$(id -un)
  service=$(mktemp)
  timer=$(mktemp)
  trap 'rm -f "$service" "$timer"' EXIT
  cat > "$service" <<UNIT
[Unit]
Description=Collect idle generated builds and owned CI archives
[Service]
Type=oneshot
User=$user
Environment=HOME=$HOME
ExecStart=$install_dir/run.sh
TimeoutStartSec=1800
Nice=10
IOSchedulingClass=idle
UNIT
  cat > "$timer" <<UNIT
[Unit]
Description=Collect build storage every fifteen minutes
[Timer]
OnBootSec=2min
OnUnitInactiveSec=15min
Persistent=true
[Install]
WantedBy=timers.target
UNIT
  sudo -n install -m 644 "$service" /etc/systemd/system/build-storage-gc.service
  sudo -n install -m 644 "$timer" /etc/systemd/system/build-storage-gc.timer
  sudo -n systemctl daemon-reload
  sudo -n systemctl enable --now build-storage-gc.timer
  sudo -n systemctl start --no-block build-storage-gc.service
fi
echo "Installed automatic collection every 15 minutes. Log: $install_dir/collector.log"
