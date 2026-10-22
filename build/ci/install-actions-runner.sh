#!/usr/bin/env bash
# Run as root on Nimbus. Read a SHORT-LIVED org registration token from stdin, never argv.
# Creates one NEW dedicated runner; never replaces/restarts the existing Skribera runners.
set -euo pipefail
set +x
[ "$(id -u)" -eq 0 ] || { echo 'Run as root' >&2; exit 1; }
[ "$(uname -s)-$(uname -m)" = Linux-x86_64 ] || { echo 'Expected Linux x64' >&2; exit 1; }

runner_user=eonic-ci
runner_root=/var/lib/eonic-ci/runner
version=2.338.0
digest=af4b794c1bc41d73d40535e3fe092a39f9679cd8d965954c2aca25a05ca41d32
group=ci-public-reviewed
name=nimbus-org-linux-1

[ ! -e "$runner_root/.runner" ] || { echo 'Runner already configured; inspect before changing it' >&2; exit 1; }
if id "$runner_user" >/dev/null 2>&1; then
  [ "$(getent passwd "$runner_user" | cut -d: -f6)" = /var/lib/eonic-ci ] || exit 1
else
  useradd --create-home --home-dir /var/lib/eonic-ci --shell /bin/bash "$runner_user"
fi
# Docker socket access is ROOT-EQUIVALENT. Only reviewed, trusted code may use this pool.
usermod --append --groups docker "$runner_user"
install -d -m 0750 -o "$runner_user" -g "$runner_user" "$runner_root"
archive=$(mktemp)
trap 'rm -f "$archive"' EXIT
curl --fail --silent --show-error --location --retry 3 \
  "https://github.com/actions/runner/releases/download/v$version/actions-runner-linux-x64-$version.tar.gz" \
  --output "$archive"
printf '%s  %s\n' "$digest" "$archive" | sha256sum --check --status
tar -xzf "$archive" -C "$runner_root"
chown -R "$runner_user:$runner_user" "$runner_root"
cd "$runner_root"

IFS= read -r ACTIONS_RUNNER_INPUT_TOKEN
[ -n "$ACTIONS_RUNNER_INPUT_TOKEN" ] || { echo 'Missing registration token on stdin' >&2; exit 1; }
export ACTIONS_RUNNER_INPUT_TOKEN
runuser -u "$runner_user" -- ./config.sh --unattended --url https://github.com/eoniclabs \
  --name "$name" --runnergroup "$group" --labels linux-docker --work _work
unset ACTIONS_RUNNER_INPUT_TOKEN
./svc.sh install "$runner_user"
./svc.sh start
