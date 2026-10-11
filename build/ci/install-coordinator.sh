#!/usr/bin/env bash
# Installs the MethodCache CI coordinator on Megatron (macOS) as a LaunchAgent that runs every minute.
#
#   build/ci/install-coordinator.sh            check the servers, install, start
#   build/ci/install-coordinator.sh --check    only check that this Mac, Nimbus and baloo have what CI needs
#   build/ci/install-coordinator.sh --remove   stop and remove the LaunchAgent (results and caches stay)
#
# Install from an up-to-date checkout of main: the coordinator is copied, so pull requests cannot change it.
# Settings go in ~/.config/methodcache-ci/env (written with the defaults on first install).
set -euo pipefail

# Over SSH, macOS does not run a login shell: add Homebrew (gh) and the .NET SDKs to PATH
export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:/usr/local/bin:$PATH"

label=com.eoniclabs.methodcache.ci
plist="$HOME/Library/LaunchAgents/$label.plist"
install_dir="$HOME/.local/share/methodcache-ci"
config_dir="$HOME/.config/methodcache-ci"
state="$HOME/.cache/methodcache-ci"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ "${1:-}" = --remove ]; then
  launchctl bootout "gui/$(id -u)/$label" 2> /dev/null || true
  rm -f "$plist"
  echo "Removed $label"
  exit 0
fi

mkdir -p "$config_dir"
if [ ! -f "$config_dir/env" ]; then
  cat > "$config_dir/env" <<'ENV'
# MethodCache CI settings, read by the coordinator on every pass
METHODCACHE_CI_REPO=eoniclabs/MethodCache
METHODCACHE_CI_NIMBUS=agent-admin@100.103.39.117
METHODCACHE_CI_WINDOWS=bldr@baloo
# Skribera's capacity lease on Nimbus, held shared so Skribera image builds wait for MethodCache runs
METHODCACHE_CI_NIMBUS_SHARED_LOCK=/srv/skribera-ci/ci-build-capacity.lock
ENV
fi
# shellcheck source=/dev/null
. "$config_dir/env"
nimbus=${METHODCACHE_CI_NIMBUS:-agent-admin@100.103.39.117}
windows=${METHODCACHE_CI_WINDOWS:-bldr@baloo}

problems=0
check() { # description command...
  if "${@:2}" > /dev/null 2>&1; then echo "  ok    $1"; else echo "  MISSING $1"; problems=1; fi
}
sdks_ok() { # the `dotnet --list-sdks` output on stdin has 8, 9 and 10
  local sdks; sdks=$(tr -d '\r')
  for major in 8 9 10; do grep -q "^$major\." <<< "$sdks" || return 1; done
}
local_sdks() { dotnet --list-sdks | sdks_ok; }

echo "This Mac ($(hostname -s))"
check "gh is signed in" gh auth status
check "git" git --version
check ".NET SDKs 8, 9 and 10" local_sdks
echo "Nimbus ($nimbus)"
check "SSH" ssh -o BatchMode=yes -o ConnectTimeout=15 "$nimbus" true
check "Docker" ssh -o BatchMode=yes "$nimbus" docker info
check "git and flock" ssh -o BatchMode=yes "$nimbus" 'git --version && flock --version'
echo "baloo ($windows)"
check "SSH" ssh -o BatchMode=yes -o ConnectTimeout=15 "$windows" "powershell -NoProfile -Command exit 0"
check "git" ssh -o BatchMode=yes "$windows" "git --version"
check "tar" ssh -o BatchMode=yes "$windows" "tar --version"
echo "  (baloo's .NET SDKs: each Windows run installs any missing SDK under C:\\mc\\dotnet itself, without administrator rights)"

if [ "${1:-}" = --check ]; then exit "$problems"; fi
if [ "$problems" -ne 0 ]; then
  echo "Fix the missing items above, then run this again."
  exit 1
fi

mkdir -p "$install_dir" "$state" "$HOME/Library/LaunchAgents"
install -m 755 "$here/coordinator.sh" "$install_dir/coordinator.sh"
git -C "$here" rev-parse HEAD > "$install_dir/installed-from" 2> /dev/null || true

# launchd starts with a minimal PATH; the coordinator adds Homebrew and .NET itself, this keeps the rest
cat > "$plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$label</string>
  <key>ProgramArguments</key>
  <array><string>/bin/bash</string><string>$install_dir/coordinator.sh</string></array>
  <key>StartInterval</key><integer>60</integer>
  <key>RunAtLoad</key><true/>
  <key>EnvironmentVariables</key>
  <dict><key>PATH</key><string>$PATH</string></dict>
  <key>StandardOutPath</key><string>$HOME/Library/Logs/methodcache-ci.log</string>
  <key>StandardErrorPath</key><string>$HOME/Library/Logs/methodcache-ci.log</string>
  <key>ProcessType</key><string>Background</string>
</dict>
</plist>
PLIST

launchctl bootout "gui/$(id -u)/$label" 2> /dev/null || true
launchctl bootstrap "gui/$(id -u)" "$plist"
echo "Installed $label from $(cat "$install_dir/installed-from" 2> /dev/null || echo 'this checkout')."
echo "Log: ~/Library/Logs/methodcache-ci.log. Run one PR now: $install_dir/coordinator.sh --pr <number>"
