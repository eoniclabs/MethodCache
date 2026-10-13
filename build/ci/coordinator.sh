#!/usr/bin/env bash
# The MethodCache CI coordinator. Runs on Megatron (macOS) from a LaunchAgent every minute: it finds open pull
# requests and the head of main that have no result yet, runs the CI jobs on the build servers and posts the
# results to GitHub as commit statuses plus a comment on the pull request.
#
#   Linux    Nimbus over SSH: build/ci/linux.sh in Docker
#   Windows  baloo over SSH: build/ci/windows.ps1
#   macOS    this Mac: build/ci/jobs.sh build unit
#
#   coordinator.sh                 one pass over open PRs and main (what the LaunchAgent runs)
#   coordinator.sh --pr <number>   run one PR now, also a PR from a fork or one that already has a result
#
# Pull requests from forks never run automatically: their code would run on our servers. Read the diff, then
# start them with --pr.
#
# The installed copy (build/ci/install-coordinator.sh) is what runs, not the one in a pull request, so a pull
# request cannot change how it is tested. Settings come from ~/.config/methodcache-ci/env (see the installer).
set -uo pipefail

# launchd and non-login SSH sessions on macOS do not see Homebrew (gh) or the .NET SDKs
export PATH="/opt/homebrew/bin:/usr/local/share/dotnet:/usr/local/bin:$PATH"

config="${METHODCACHE_CI_CONFIG:-$HOME/.config/methodcache-ci/env}"
# shellcheck source=/dev/null
[ -f "$config" ] && . "$config"

repo="${METHODCACHE_CI_REPO:-eoniclabs/MethodCache}"
clone_url="${METHODCACHE_CI_CLONE_URL:-https://github.com/$repo.git}"
nimbus="${METHODCACHE_CI_NIMBUS:-agent-admin@100.103.39.117}"
nimbus_root="${METHODCACHE_CI_NIMBUS_ROOT:-methodcache-ci}"            # relative to the SSH user's home
nimbus_lock="${METHODCACHE_CI_NIMBUS_SHARED_LOCK-/srv/skribera-ci/ci-build-capacity.lock}"
windows="${METHODCACHE_CI_WINDOWS:-bldr@baloo}"
windows_root="${METHODCACHE_CI_WINDOWS_ROOT:-C:\\mc}"
state="${METHODCACHE_CI_STATE:-$HOME/.cache/methodcache-ci}"
timeout_seconds="${METHODCACHE_CI_TIMEOUT_SECONDS:-3600}"
keep_runs_days="${METHODCACHE_CI_KEEP_RUNS_DAYS:-3}"
max_macos_mib="${METHODCACHE_CI_MAX_MACOS_MIB:-10240}"
keep_failed_hours="${METHODCACHE_CI_KEEP_FAILED_HOURS:-24}"
retry_limit="${METHODCACHE_CI_MAX_ATTEMPTS:-5}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# A server that stops answering ends the SSH session within two minutes instead of never
ssh_options=(-o BatchMode=yes -o ConnectTimeout=30 -o ServerAliveInterval=30 -o ServerAliveCountMax=4)

contexts=("MethodCache CI (Linux)" "MethodCache CI (Windows)" "MethodCache CI (macOS)")
aggregate="MethodCache CI"

mkdir -p "$state/done" "$state/runs"
log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*"; }

# One coordinator at a time; a scheduled pass that finds another one running just ends, a manual --pr run waits
# for it (macOS has no flock(1); mkdir is atomic, and a lock whose process is gone is taken over)
lock="$state/coordinator.lock"
until mkdir "$lock" 2> /dev/null; do
  if ! kill -0 "$(cat "$lock/pid" 2> /dev/null)" 2> /dev/null; then
    rm -rf "$lock"
  elif [ "${1:-}" = --pr ]; then
    [ -n "${waiting:-}" ] || { waiting=1; log "waiting for the running pass to finish"; }
    sleep 30
  else
    exit 0
  fi
done
echo $$ > "$lock/pid"
trap 'rm -rf "$lock"' EXIT

status_failed=0
post_status() { # sha context state description
  gh api --silent "repos/$repo/statuses/$1" -f state="$3" -f context="$2" -f description="${4:0:140}" \
    && return 0
  log "could not post status $2=$3 for $1"
  status_failed=1
}

kill_tree() { # pid: the process and everything it started
  local child
  for child in $(pgrep -P "$1"); do kill_tree "$child"; done
  kill -TERM "$1" 2> /dev/null
}

with_timeout() { # seconds command...; macOS has no timeout(1) by default
  local seconds=$1; shift
  # A background command gets /dev/null as stdin in macOS's bash 3.2 unless it is redirected explicitly, which
  # would hand `ssh ... bash -s` an empty script; fd 7 passes the caller's stdin through
  exec 7<&0
  "$@" <&7 7<&- &
  local pid=$!
  exec 7<&-
  # The watchdog writes to stderr, so a pipe on stdout (the log copy into tar) ends with the command
  ( sleep "$seconds" && log "timed out after $seconds s: $*" && kill_tree "$pid" ) >&2 7<&- &
  local watchdog=$!
  wait "$pid"; local code=$?
  # Killing only the watchdog shell leaves its sleep holding inherited pipe descriptors open.
  kill_tree "$watchdog"; wait "$watchdog" 2> /dev/null
  return "$code"
}

# The checkout steps every platform runs: a mirror kept between runs, then a clone of the exact commit
# (full history, which Nerdbank.GitVersioning needs).
checkout_script() { # root sha run_id
  cat <<SCRIPT
set -euo pipefail
cd ~ && mkdir -p '$1/runs' && cd '$1'
[ -d mirror.git ] || git clone --quiet --mirror '$clone_url' mirror.git
git -C mirror.git fetch --quiet --prune origin '+refs/heads/*:refs/heads/*' '+refs/pull/*/head:refs/pull/*/head'
# macOS is collected by the owned-checkout collector; never age-delete an active local checkout.
if [ '$1' != '$state/macos' ]; then
  find runs -mindepth 1 -maxdepth 1 -mtime +$keep_runs_days -exec rm -rf {} +
fi
# A plain local clone (hard links), not --shared: Nerdbank.GitVersioning cannot read objects through alternates
rm -rf 'runs/$3' && git clone --quiet --no-checkout mirror.git 'runs/$3'
git -C 'runs/$3' checkout --quiet --detach '$2'
cd 'runs/$3'
SCRIPT
}

run_linux() { # sha run_id results_dir
  {
    checkout_script "$nimbus_root" "$1" "$2"
    cat <<SCRIPT
export METHODCACHE_CI_RUN_ID='$2' METHODCACHE_CI_CACHE=~/'$nimbus_root/cache'
# Always held when configured: a missing lease directory fails the run instead of building without the lease
${nimbus_lock:+export METHODCACHE_CI_SHARED_LOCK='$nimbus_lock'}
code=0
nice -n 10 build/ci/linux.sh || code=\$?
# Written only when every job got its turn, so a dropped connection or timeout is retried
echo "\$code" > artifacts/ci/finished
exit "\$code"
SCRIPT
  } | with_timeout "$timeout_seconds" ssh "${ssh_options[@]}" "$nimbus" bash -s > "$3/console.log" 2>&1
  local code=$?
  # Preserve both SSH and extraction diagnostics: an empty archive must not look like a job that never ran.
  fetch_linux_results "$2" "$3" "$1" || return 75
  return "$code"
}

fetch_linux_results() { # run_id results_dir sha; read-only on Nimbus
  with_timeout 300 ssh "${ssh_options[@]}" "$nimbus" "git -C ~/'$nimbus_root/runs/$1' log -1 --format=%H | grep -qx '$3' && cd ~/'$nimbus_root/runs/$1/artifacts/ci' && tar -cf - *.log *.exit finished" \
    < /dev/null 2>> "$2/transfer.log" | tar -xf - -C "$2" 2>> "$2/transfer.log"
  local codes=("${PIPESTATUS[@]}")
  if [ "${codes[0]}" -ne 0 ] || [ "${codes[1]}" -ne 0 ]; then
    log "Linux result transfer failed for $1 (SSH ${codes[0]}, tar ${codes[1]}); see $2/transfer.log"
    return 1
  fi
  finished "$2"
}

run_windows() { # sha run_id results_dir
  local body script
  # A quoted heredoc, so no PowerShell $ needs escaping (read, not $(cat ...), which macOS's bash 3.2 misparses)
  IFS= read -r -d '' body <<'SCRIPT' || true
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'runs') | Out-Null
Set-Location $Root
function Invoke-Native { & $args[0] $args[1..($args.Count - 1)]; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }

# The .NET 8, 9 and 10 SDKs in a folder of our own, so CI needs no administrator and leaves the machine's alone
$dotnet = Join-Path $Root 'dotnet'
$sdks = if (Test-Path "$dotnet\dotnet.exe") { & "$dotnet\dotnet.exe" --list-sdks } else { @() }
foreach ($channel in '8.0', '9.0', '10.0') {
    if (-not ($sdks -match "^$([regex]::Escape($channel))\.")) {
        if (-not (Test-Path dotnet-install.ps1)) { Invoke-WebRequest -UseBasicParsing https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1 }
        & powershell -NoProfile -ExecutionPolicy Bypass -File dotnet-install.ps1 -Channel $channel -InstallDir $dotnet -NoPath
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
$env:DOTNET_ROOT = $dotnet
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:PATH = "$dotnet;$env:PATH"

if (-not (Test-Path mirror.git)) { Invoke-Native git clone --quiet --mirror $CloneUrl mirror.git }
Invoke-Native git -C mirror.git fetch --quiet --prune origin '+refs/heads/*:refs/heads/*' '+refs/pull/*/head:refs/pull/*/head'
Get-ChildItem runs -Directory | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$KeepDays) } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
$run = Join-Path $Root "runs\$RunId"
if (Test-Path $run) { Remove-Item -Recurse -Force $run }
Invoke-Native git -c core.longpaths=true clone --quiet --no-checkout mirror.git $run
Invoke-Native git -C $run -c core.longpaths=true checkout --quiet --detach $Sha

$results = Join-Path $run 'artifacts\ci'
$env:METHODCACHE_CI_RESULTS = $results
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $run 'build\ci\windows.ps1')
$code = $LASTEXITCODE
# Written only when every job got its turn, so a dropped connection or timeout is retried
if (Test-Path $results) { Set-Content -Path (Join-Path $results 'finished') -Value $code -Encoding ascii }

# The job logs come back as base64 text between two markers: text survives the PowerShell of the SSH session
if (Test-Path $results) {
    $zip = "$results.zip"
    Compress-Archive -Path "$results\*" -DestinationPath $zip -Force
    '@@methodcache-logs-begin@@'
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($zip), 'InsertLineBreaks')
    '@@methodcache-logs-end@@'
}
exit $code
SCRIPT
  script="\$Root = '$windows_root'; \$Sha = '$1'; \$RunId = '$2'; \$CloneUrl = '$clone_url'; \$KeepDays = $keep_runs_days
$body"
  local encoded
  encoded=$(printf '%s' "$script" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n')
  with_timeout "$timeout_seconds" ssh "${ssh_options[@]}" "$windows" "powershell -NoProfile -EncodedCommand $encoded" \
    < /dev/null > "$3/output.log" 2>&1
  local code=$?
  sed '/^@@methodcache-logs-begin@@/,/^@@methodcache-logs-end@@/d' "$3/output.log" > "$3/console.log"
  sed -n '/^@@methodcache-logs-begin@@/,/^@@methodcache-logs-end@@/p' "$3/output.log" | sed '1d;$d' | tr -d '\r' \
    > "$3/logs.b64"
  # -d is GNU and macOS 13+; older macOS only knows -D
  { base64 -d < "$3/logs.b64" || base64 -D < "$3/logs.b64"; } > "$3/logs.zip" 2> /dev/null \
    && unzip -o -q "$3/logs.zip" -d "$3" 2> /dev/null
  rm -f "$3/output.log" "$3/logs.b64" "$3/logs.zip"
  return "$code"
}

run_macos() { # sha run_id results_dir
  with_timeout "$timeout_seconds" run_macos_jobs "$@"
}

run_macos_jobs() { # sha run_id results_dir
  (
    eval "$(checkout_script "$state/macos" "$1" "$2")"
    METHODCACHE_CI_RESULTS="$3" FRAMEWORK='' bash -c '
      for job in build unit; do
        if [ "$job" = unit ] && [ "$(cat "$METHODCACHE_CI_RESULTS/build.exit" 2> /dev/null)" != 0 ]; then
          echo skipped > "$METHODCACHE_CI_RESULTS/unit.exit"; continue
        fi
        build/ci/jobs.sh "$job" > "$METHODCACHE_CI_RESULTS/$job.log" 2>&1
        echo $? > "$METHODCACHE_CI_RESULTS/$job.exit"
      done
      # Written only when every job got its turn, so a timeout is retried
      touch "$METHODCACHE_CI_RESULTS/finished"
      ! grep -qvx 0 "$METHODCACHE_CI_RESULTS"/*.exit'
  ) > "$3/console.log" 2>&1
}

# "build ✓, tests ✗, docs skipped" from the .exit files of one platform, in the order the jobs ran
summary_of() { # results_dir
  local summary="" file job code
  for file in "$1"/*.exit; do
    [ -f "$file" ] || continue
    job=$(basename "$file" .exit); code=$(tr -d '[:space:]' < "$file")
    case "$code" in
      0) summary+="${summary:+, }$job ✓" ;;
      skipped) summary+="${summary:+, }$job skipped" ;;
      *) summary+="${summary:+, }$job ✗" ;;
    esac
  done
  echo "${summary:-no results}"
}

# The lines of a job log worth reading first: errors and failed tests, else the end of the log
excerpt_of() { # log_file
  local errors
  errors=$(grep -E ': error |\[FAIL\]|^ +Failed |^Failed!|Error Message:|Assert\.' "$1" 2> /dev/null | grep -v '^\s*at ' | sort -u | head -n 40)
  if [ -n "$errors" ]; then echo "$errors"; else grep -v '^\s*$' "$1" 2> /dev/null | tail -n 40; fi | cut -c1-300
}

# The platform script returned, with at least one job result (not killed, disconnected or stopped before any job)
finished() { # results_dir
  python3 "$here/coordinator-state.py" complete "$1"
}

failed_jobs_of() { # results_dir
  local file
  for file in "$1"/*.exit; do
    [ -e "$file" ] || continue
    case "$(tr -d '[:space:]' < "$file")" in 0|skipped) ;; *) basename "$file" .exit ;; esac
  done
}

run_commit() { # sha pr(optional)
  local sha=$1 pr=${2:-}
  local run_id
  run_id="$(date '+%Y%m%d-%H%M%S')-${sha:0:12}"
  local run_dir="$state/runs/$run_id"
  mkdir -p "$run_dir/linux" "$run_dir/windows" "$run_dir/macos"
  printf '%s\n' "$sha" > "$run_dir/commit"
  log "run $run_id for ${pr:+PR #$pr }$sha"

  status_failed=0
  local context
  for context in "$aggregate" "${contexts[@]}"; do post_status "$sha" "$context" pending "Running on the build servers"; done
  local started=$SECONDS

  run_platform linux "$sha" "$run_id" "$run_dir/linux" & local linux_pid=$!
  run_platform windows "$sha" "$run_id" "$run_dir/windows" & local windows_pid=$!
  run_platform macos "$sha" "$run_id" "$run_dir/macos" & local macos_pid=$!

  local all_ok=1 platform pid code
  for platform in linux windows macos; do
    case $platform in linux) pid=$linux_pid; context=${contexts[0]} ;; windows) pid=$windows_pid; context=${contexts[1]} ;;
                      macos) pid=$macos_pid; context=${contexts[2]} ;; esac
    wait "$pid"; code=$?
    if [ "$code" -eq 0 ] && python3 "$here/coordinator-state.py" passed "$run_dir/$platform"; then
      post_status "$sha" "$context" success "$(summary_of "$run_dir/$platform")"
    else
      all_ok=0
      post_status "$sha" "$context" failure "$(summary_of "$run_dir/$platform") (exit $code)"
    fi
  done

  local minutes=$(( (SECONDS - started + 59) / 60 ))
  if [ "$all_ok" -eq 1 ]; then
    post_status "$sha" "$aggregate" success "All platforms passed in $minutes min"
  else
    post_status "$sha" "$aggregate" failure "A platform failed, see the PR comment ($minutes min)"
  fi
  # A platform that did not finish all its jobs (a server was unreachable, the connection dropped, a timeout), or
  # a result GitHub did not accept, is retried after 30 minutes
  if [ "$status_failed" -eq 1 ] || ! finished "$run_dir/linux" || ! finished "$run_dir/windows" || ! finished "$run_dir/macos"; then
    echo retry > "$state/done/$sha"
    local retry_args=(retry --state "$state" --sha "$sha" --limit "$retry_limit")
    [ "${force_run:-0}" -eq 1 ] && retry_args+=(--reset)
    python3 "$here/coordinator-state.py" "${retry_args[@]}"
  else
    echo 'done' > "$state/done/$sha"
    rm -f "$state/retries/$sha.json"
  fi

  [ -n "$pr" ] && comment_on "$pr" "$sha" "$run_dir" "$all_ok" "$minutes" "$run_id"
  log "run $run_id finished: $([ "$all_ok" -eq 1 ] && echo passed || echo failed)"
  collect_macos
}

run_platform() { # platform sha run_id results_dir
  local platform=$1; shift
  if [ "${force_run:-0}" -ne 1 ] && python3 "$here/coordinator-state.py" reuse --state "$state" \
      --sha "$1" --platform "$platform" --destination "$3"; then
    log "reused completed $platform results for $1"
    return 0
  fi
  # A previous Linux build may be complete on Nimbus while its transfer was lost. Try collection before rebuilding.
  if [ "$platform" = linux ] && [ "${force_run:-0}" -ne 1 ]; then
    local previous
    while IFS= read -r previous; do
      [ -n "$previous" ] || continue
      if fetch_linux_results "$previous" "$3" "$1"; then
        log "recovered Linux results from $previous"
        python3 "$here/coordinator-state.py" passed "$3"; return $?
      fi
      # Don't mix a partial archive from one run with a later fresh build.
      rm -f "$3"/*.exit "$3/finished"
    done < <(python3 "$here/coordinator-state.py" previous --state "$state" --sha "$1" --exclude-run "$2")
  fi
  "run_$platform" "$@"
}

collect_macos() {
  python3 "$here/coordinator-state.py" gc --state "$state" --keep-days "$keep_runs_days" \
    --keep-failed-hours "$keep_failed_hours" --max-mib "$max_macos_mib" || log "macOS checkout collection failed"
}

comment_on() { # pr sha run_dir all_ok minutes run_id
  local body platform job truncated=0
  # GitHub rejects a comment over 65,536 characters, so excerpts stop at a budget and the rest is in the logs
  add_excerpt() {
    if [ $(( ${#body} + ${#1} )) -le 60000 ]; then body+=$1
    elif [ "$truncated" -eq 0 ]; then truncated=1; body+="

More failures than fit in a comment: see the logs on Megatron."
    fi
  }
  body="### $([ "$4" -eq 1 ] && echo '✅ MethodCache CI passed' || echo '❌ MethodCache CI failed') on \`${2:0:12}\`

| Platform | Jobs |
|---|---|
| Linux (Nimbus) | $(summary_of "$3/linux") |
| Windows (baloo) | $(summary_of "$3/windows") |
| macOS (Megatron) | $(summary_of "$3/macos") |

$5 min, run \`$6\`. Full logs are on Megatron in \`~/.cache/methodcache-ci/runs/$6\`."
  for platform in linux windows macos; do
    local failed
    failed=$(failed_jobs_of "$3/$platform")
    if [ -z "$failed" ] && ! ls "$3/$platform"/*.exit > /dev/null 2>&1; then
      add_excerpt "

<details><summary>$platform: no job ran</summary>

\`\`\`
$(tail -n 40 "$3/$platform/console.log" 2> /dev/null | cut -c1-300)
\`\`\`
</details>"
    fi
    for job in $failed; do
      add_excerpt "

<details><summary>$platform: $job</summary>

\`\`\`
$(excerpt_of "$3/$platform/$job.log")
\`\`\`
</details>"
    done
  done
  local attempt
  for attempt in 1 2 3; do
    gh api --silent "repos/$repo/issues/$1/comments" -f body="$body" && return 0
    log "could not comment on PR #$1 (attempt $attempt)"
    sleep $((attempt * 20))
  done
}

if [ "${1:-}" = --pr ]; then
  force_run=1
  pr=${2:?--pr needs a pull request number}
  sha=$(gh api "repos/$repo/pulls/$pr" --jq .head.sha) || exit 1
  run_commit "$sha" "$pr"
  exit 0
fi

collect_macos

# A commit has a result, unless its last run could not reach a server and that was over 30 minutes ago
already_ran() { # sha
  local marker="$state/done/$1"
  [ -e "$marker" ] || return 1
  [ "$(cat "$marker")" = retry ] && python3 "$here/coordinator-state.py" retry-due --state "$state" --sha "$1" && return 1
  return 0
}

# Newest main first, then open PRs from this repository, each at most once per commit
main_sha=$(gh api "repos/$repo/commits/main" --jq .sha) || exit 1
already_ran "$main_sha" || run_commit "$main_sha"

# Every open PR whose branch is in this repository (a fork's head.repo is another one, or null once deleted)
pull_requests=$(gh api --paginate "repos/$repo/pulls?state=open&per_page=100" \
  --jq ".[] | select(.head.repo.full_name == \"$repo\") | \"\(.number) \(.head.sha)\"") || exit 1
while read -r pr sha; do
  [ -n "$sha" ] || continue
  already_ran "$sha" && continue
  run_commit "$sha" "$pr" < /dev/null
done <<< "$pull_requests"

find "$state/runs" -mindepth 1 -maxdepth 1 -mtime +14 -exec rm -rf {} + 2> /dev/null
find "$state/done" -type f -mtime +30 -delete 2> /dev/null
exit 0
