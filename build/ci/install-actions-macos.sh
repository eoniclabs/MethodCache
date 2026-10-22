#!/usr/bin/env bash
# Run as the intended build user. Pass a short-lived organization registration token on stdin.
# Usage: install-actions-macos.sh megatron (or tigger); no sudo or machine-wide changes.
set -euo pipefail
export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:/usr/local/bin:$PATH"
host=${1:?Expected reviewed host label megatron or tigger}
case "$host" in megatron|tigger) ;; *) echo 'Unapproved host label' >&2; exit 64 ;; esac
[ "$(uname -s)" = Darwin ] || { echo 'macOS required' >&2; exit 1; }
case "$(uname -m)" in
  arm64) arch=arm64; checksum=df4cebda25c86a886ed204e49fee63f5c2e7cec5f447b5c98440a826bbdf9df2 ;;
  x86_64) arch=x64; checksum=dea7a58796ce215fc424a8b27c0fdd9b813fa5fc03d109a4a1d38131d6bfa1a3 ;;
  *) echo 'Unsupported architecture' >&2; exit 1 ;;
esac
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root="$HOME/.local/share/eonic-ci"
runner="$root/runner"
[ ! -e "$runner/.runner" ] || { echo 'Runner already configured; inspect instead of overwriting' >&2; exit 1; }
command -v python3 >/dev/null
dotnet --version
umask 077
mkdir -p "$runner" "$HOME/.cache/eonic-ci"
archive=$(mktemp "${TMPDIR:-/tmp}/actions-runner.XXXXXX")
trap 'rm -f "$archive"' EXIT
curl --fail --location --retry 3 "https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-osx-$arch-2.338.0.tar.gz" -o "$archive"
printf '%s  %s\n' "$checksum" "$archive" | shasum -a 256 -c -
tar -xzf "$archive" -C "$runner"
install -m 755 "$here/host-lease.py" "$root/host-lease.py"
printf 'EONIC_CI_HOST_LEASE=%s\nEONIC_CI_CAPACITY_LOCKS=%s:%s\n' \
  "$root/host-lease.py" "$HOME/.cache/eonic-ci/capacity.lock" \
  "$HOME/.cache/skribera-external-ci/.coordinator.lock" > "$runner/.env"
IFS= read -r ACTIONS_RUNNER_INPUT_TOKEN
[ -n "$ACTIONS_RUNNER_INPUT_TOKEN" ]
export ACTIONS_RUNNER_INPUT_TOKEN
cd "$runner"
./config.sh --unattended --url https://github.com/eoniclabs --name "$host-org-macos-1" \
  --runnergroup ci-public-reviewed --labels "macos-build,macos-$arch,$host" --work _work
unset ACTIONS_RUNNER_INPUT_TOKEN
./svc.sh install
./svc.sh start
./svc.sh status
