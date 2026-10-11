#!/usr/bin/env bash
# Runs the Linux CI jobs in the build image (build/ci/Dockerfile), on a build server or any machine with Docker.
# Redis and SQL Server for the package apps run as containers labelled with this run, removed at the end; the
# integration tests start their own through Testcontainers on the same Docker.
#
#   build/ci/linux.sh                  every Linux job
#   build/ci/linux.sh build tests      only these jobs (see build/ci/jobs.sh)
#
# Each job's log and exit code go to $METHODCACHE_CI_RESULTS (default artifacts/ci): <job>.log and <job>.exit.
# A later job still runs when an earlier one fails, except that nothing runs after a failed build.
#
# Settings (environment):
#   METHODCACHE_CI_RUN_ID       label for the containers of this run (default: local-<pid>)
#   METHODCACHE_CI_CACHE        NuGet package cache kept between runs (default: ~/.cache/methodcache-ci)
#   METHODCACHE_CI_SHARED_LOCK  a lock file to hold shared while running, e.g. a build server's capacity lease
#   METHODCACHE_CI_DOCKER_ARGS  extra `docker run` arguments, e.g. proxy settings
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
jobs=("$@")
if [ ${#jobs[@]} -eq 0 ]; then
  jobs=(build demo tests cultures aot packages docs)
fi

run_id="${METHODCACHE_CI_RUN_ID:-local-$$}"
cache_dir="${METHODCACHE_CI_CACHE:-$HOME/.cache/methodcache-ci}"
results_dir="${METHODCACHE_CI_RESULTS:-$repo_dir/artifacts/ci}"
label="methodcache.ci.run=$run_id"
mkdir -p "$cache_dir/nuget" "$results_dir"

if [ -n "${METHODCACHE_CI_SHARED_LOCK:-}" ]; then
  exec 9> "$METHODCACHE_CI_SHARED_LOCK"
  echo "Waiting for a shared hold on $METHODCACHE_CI_SHARED_LOCK"
  flock --shared --timeout 1800 9
fi

image="methodcache-ci:$(sha256sum "$repo_dir/build/ci/Dockerfile" | cut -c1-12)"
if ! docker image inspect "$image" > /dev/null 2>&1; then
  docker build --tag "$image" "$repo_dir/build/ci"
fi

cleanup() {
  local containers
  containers=$(docker ps --all --quiet --filter "label=$label")
  if [ -n "$containers" ]; then docker rm --force $containers > /dev/null; fi
}
trap cleanup EXIT

if printf '%s\n' "${jobs[@]}" | grep -qx packages; then
  echo "Starting Redis and SQL Server for the package apps"
  docker run --detach --label "$label" --publish 127.0.0.1::6379 --name "methodcache-ci-redis-$run_id" \
    redis:7-alpine > /dev/null
  sql_password="Ci_$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')!"
  docker run --detach --label "$label" --publish 127.0.0.1::1433 --name "methodcache-ci-sql-$run_id" \
    --env ACCEPT_EULA=Y --env MSSQL_SA_PASSWORD="$sql_password" \
    mcr.microsoft.com/mssql/server:2022-latest > /dev/null
  redis_port=$(docker port "methodcache-ci-redis-$run_id" 6379/tcp | head -1 | sed 's/.*://')
  sql_port=$(docker port "methodcache-ci-sql-$run_id" 1433/tcp | head -1 | sed 's/.*://')
  export METHODCACHE_REDIS_URL="localhost:$redis_port"
  export METHODCACHE_SQLSERVER_CONNECTION="Server=localhost,$sql_port;User Id=sa;Password=$sql_password;TrustServerCertificate=True;Encrypt=False"
fi

# Host network: the jobs reach the containers above, and those Testcontainers starts, on localhost.
# The job runs as the calling user, so everything it writes in the checkout stays removable.
docker_args=(
  --rm --network host
  --user "$(id -u):$(id -g)"
  --group-add "$(stat -c %g /var/run/docker.sock)"
  --volume /var/run/docker.sock:/var/run/docker.sock
  --volume "$repo_dir:$repo_dir" --workdir "$repo_dir"
  --volume "$cache_dir/nuget:/nuget"
  --env TESTCONTAINERS_HOST_OVERRIDE=localhost
  --env METHODCACHE_REDIS_URL --env METHODCACHE_SQLSERVER_CONNECTION
  --label "$label"
)
# shellcheck disable=SC2206 # the extra arguments are meant to split on spaces
docker_args+=(${METHODCACHE_CI_DOCKER_ARGS:-})

failed=0
for job in "${jobs[@]}"; do
  rm -f "$results_dir/$job.exit"
  if [ "$job" != build ] && [ -f "$results_dir/build.exit" ] && [ "$(cat "$results_dir/build.exit")" != 0 ]; then
    echo "skipped: the build failed" > "$results_dir/$job.log"
    echo skipped > "$results_dir/$job.exit"
    continue
  fi
  echo "=== $job (log: $results_dir/$job.log)"
  started=$SECONDS
  set +e
  docker run "${docker_args[@]}" "$image" build/ci/jobs.sh "$job" > "$results_dir/$job.log" 2>&1
  code=$?
  set -e
  echo "$code" > "$results_dir/$job.exit"
  echo "    exit $code after $((SECONDS - started)) s"
  if [ "$code" -ne 0 ]; then
    failed=1
    tail -n 40 "$results_dir/$job.log"
  fi
done
exit "$failed"
