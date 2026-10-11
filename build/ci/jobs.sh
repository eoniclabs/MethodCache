#!/usr/bin/env bash
# The CI jobs for MethodCache. Each job is a plain dotnet/python command, so the same script runs on the
# build servers (inside the image from build/ci/Dockerfile), on a developer machine and in any CI host.
#
#   build/ci/jobs.sh build      restore and build the solution (Release)
#   build/ci/jobs.sh demo       run the console demo; each chapter checks its own claims
#   build/ci/jobs.sh tests      every test on net8.0, net9.0 and net10.0, Redis and SQL Server required
#   build/ci/jobs.sh unit       every test except the Docker-backed ones (Windows and macOS); FRAMEWORK=net10.0 picks one
#   build/ci/jobs.sh cultures   the unit tests under tr-TR and sv-SE
#   build/ci/jobs.sh aot        publish and run tests/MethodCache.AotSmokeTest with Native AOT
#   build/ci/jobs.sh packages   pack all 7 packages, install them in apps (needs METHODCACHE_REDIS_URL and
#                               METHODCACHE_SQLSERVER_CONNECTION), and build the Quick Start with the .NET 8 SDK
#   build/ci/jobs.sh docs       the docs snippets and llms.txt match their sources
#
# demo, tests, unit and cultures expect "build" to have run first in the same checkout.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# Deterministic paths in the shipped DLLs and PDBs (see Directory.Build.props)
export CI=true

solution=MethodCache.sln
without_docker="FullyQualifiedName!~MethodCache.Redis.IntegrationTests&FullyQualifiedName!~MethodCache.SqlServer.IntegrationTests"

job_build() {
  dotnet restore "$solution"
  dotnet build "$solution" --configuration Release --no-restore
}

job_demo() {
  dotnet run --project samples/MethodCache.Demo --configuration Release --no-build
}

job_tests() {
  # A missing Redis or SQL Server fails the run instead of skipping the tests
  METHODCACHE_REQUIRE_INFRA_TESTS=true \
    dotnet test "$solution" --configuration Release --no-build --results-directory artifacts/test-results --logger trx
}

job_unit() {
  local framework_args=()
  if [ -n "${FRAMEWORK:-}" ]; then framework_args=(--framework "$FRAMEWORK"); fi
  # (the ${...+...} form, because macOS's bash 3.2 treats an empty array as unset under set -u)
  dotnet test "$solution" --configuration Release --no-build ${framework_args[@]+"${framework_args[@]}"} \
    --filter "$without_docker"
}

job_cultures() {
  # Turkish (dotted/dotless i, comma decimals) and Swedish (U+2212 minus sign, comma decimals) catch
  # formatting and parsing that depends on the current culture
  for culture in tr_TR sv_SE; do
    echo "== Tests under $culture"
    LANG="$culture.UTF-8" LC_ALL="$culture.UTF-8" \
      dotnet test "$solution" --configuration Release --no-build --framework net10.0 --filter "$without_docker"
  done
}

job_aot() {
  dotnet publish tests/MethodCache.AotSmokeTest --configuration Release --runtime linux-x64 --output artifacts/aot-smoke
  artifacts/aot-smoke/MethodCache.AotSmokeTest
}

job_packages() {
  : "${METHODCACHE_REDIS_URL:?set METHODCACHE_REDIS_URL to a Redis the add-on apps can use}"
  : "${METHODCACHE_SQLSERVER_CONNECTION:?set METHODCACHE_SQLSERVER_CONNECTION to a SQL Server the add-on apps can use}"
  rm -rf artifacts/packages
  dotnet pack "$solution" --configuration Release --output artifacts/packages

  echo "== All 7 packages were packed"
  for package in MethodCache MethodCache.Redis MethodCache.SqlServer MethodCache.Serialization.MessagePack \
                 MethodCache.AspNetCore MethodCache.OpenTelemetry MethodCache.HttpCaching; do
    compgen -G "artifacts/packages/$package.[0-9]*.nupkg" > /dev/null || { echo "Missing $package"; exit 1; }
  done

  echo "== The generator, analyzers and code fixes are in the MethodCache package"
  local contents
  contents=$(python3 -c 'import sys, zipfile; print("\n".join(zipfile.ZipFile(sys.argv[1]).namelist()))' \
    artifacts/packages/MethodCache.[0-9]*.nupkg)
  for dll in analyzers/dotnet/cs/MethodCache.SourceGenerator.dll analyzers/dotnet/cs/MethodCache.Analyzers.dll \
             analyzers/dotnet/cs/MethodCache.CodeFixes.dll; do
    grep -qx "$dll" <<< "$contents" || { echo "Missing $dll"; exit 1; }
  done

  echo "== Install the package and run the Quick Start"
  dotnet run --project tests/MethodCache.PackageSmokeTest --configuration Release

  echo "== The Quick Start again with the .NET 8 SDK (the oldest compiler the generator supports)"
  (
    cd tests/MethodCache.PackageSmokeTest
    rm -rf bin obj
    trap 'rm -f global.json' EXIT
    echo '{ "sdk": { "version": "8.0.100", "rollForward": "latestFeature" } }' > global.json
    dotnet --version
    dotnet run --configuration Release
  )

  for framework in net8.0 net10.0; do
    echo "== Install the add-on packages and use them in an app ($framework)"
    dotnet run --project tests/MethodCache.PackageSmokeTest.AddOns --configuration Release --framework "$framework"
  done
}

job_docs() {
  dotnet tool restore
  dotnet mdsnippets
  git diff --exit-code || {
    echo "Docs are out of date. Run 'dotnet tool restore && dotnet mdsnippets' and commit the result."
    exit 1
  }
  python3 .github/scripts/generate-llms.py
  git diff --exit-code -- llms.txt llms-full.txt || {
    echo "llms.txt is out of date. Run 'python3 .github/scripts/generate-llms.py' and commit the result."
    exit 1
  }
}

if [ $# -eq 0 ]; then
  sed -n '2,16p' "${BASH_SOURCE[0]}"
  exit 2
fi

for job in "$@"; do
  if ! declare -F "job_$job" > /dev/null; then
    echo "Unknown job: $job" >&2
    exit 2
  fi
  echo "=== $job"
  "job_$job"
done
