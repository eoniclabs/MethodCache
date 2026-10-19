#!/usr/bin/env bash
# The CI jobs for MethodCache. Each job is a plain dotnet/python command, so the same script runs on the
# build servers (inside the image from build/ci/Dockerfile), on a developer machine and in any CI host.
#
#   build/ci/jobs.sh build      restore and build the solution (Release)
#   build/ci/jobs.sh demo       eleven-chapter console tour (including HttpClient), plus web tests without Docker
#   build/ci/jobs.sh webdemo    all web demo tests, including two instances and Redis outage/recovery
#   build/ci/jobs.sh tests      every test on net10.0, Redis and SQL Server required
#   build/ci/jobs.sh unit       every test except the Docker-backed ones (Windows and macOS), on net10.0
#   build/ci/jobs.sh cultures   the unit tests under tr-TR and sv-SE
#   build/ci/jobs.sh aot        publish and run tests/MethodCache.AotSmokeTest with Native AOT
#   build/ci/jobs.sh packages   pack all 7 packages, install them in apps (needs METHODCACHE_REDIS_URL and
#                               METHODCACHE_SQLSERVER_CONNECTION)
#   build/ci/jobs.sh docs       the docs snippets and llms.txt match their sources
#   build/ci/jobs.sh diffcoverage  at least 80% of the lines a change adds under src/ are covered by tests
#   build/ci/jobs.sh properties  the CsCheck properties from random seeds, PROPERTY_SECONDS (5) each
#   build/ci/jobs.sh soak        the soak tests for METHODCACHE_SOAK_SECONDS (30)
#
# Not in the default linux.sh list, because it takes about an hour (build/ci/README.md gives run times):
#   build/ci/jobs.sh mutation    Stryker.NET on MethodCache.Core, a report in artifacts/mutation; never fails on
#                                the score
#
# demo, tests, unit, cultures, diffcoverage, properties and soak expect "build" to have run first in the same
# checkout.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# Deterministic paths in the shipped DLLs and PDBs (see Directory.Build.props)
export CI=true
# High and critical NuGet advisories fail restore (see Directory.Build.props and README.md)
export MethodCacheEnforceAudit=true

solution=MethodCache.sln
without_docker="FullyQualifiedName!~MethodCache.Redis.IntegrationTests&FullyQualifiedName!~MethodCache.SqlServer.IntegrationTests&Category!=RedisDemo"

job_build() {
  PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s build/ci/tests -v
  PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s benchmarks/MethodCache.Benchmarks/validation -v
  dotnet restore "$solution"
  dotnet build "$solution" --configuration Release --no-restore
}

job_demo() {
  dotnet run --project samples/MethodCache.Demo --configuration Release --no-build
  dotnet test tests/MethodCache.Docs.Tests --configuration Release --no-build \
    --filter "FullyQualifiedName~WebDemo&Category!=RedisDemo"
}

job_webdemo() {
  METHODCACHE_REQUIRE_INFRA_TESTS=true \
    dotnet test tests/MethodCache.Docs.Tests --configuration Release --no-build \
      --filter "FullyQualifiedName~WebDemo" --logger "console;verbosity=detailed"
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
  rm -rf artifacts/aot-corpus
  dotnet run --project tests/MethodCache.SignatureCorpus.Export --configuration Release -- artifacts/aot-corpus
  dotnet publish artifacts/aot-corpus/Corpus.csproj --configuration Release --runtime linux-x64 --output artifacts/aot-corpus-native
  artifacts/aot-corpus-native/Corpus
}

job_packages() {
  : "${METHODCACHE_REDIS_URL:?set METHODCACHE_REDIS_URL to a Redis the add-on apps can use}"
  : "${METHODCACHE_SQLSERVER_CONNECTION:?set METHODCACHE_SQLSERVER_CONNECTION to a SQL Server the add-on apps can use}"
  rm -rf artifacts/packages
  dotnet pack "$solution" --configuration Release --output artifacts/packages
  python3 build/ci/check_package_contracts.py artifacts/packages
  python3 build/ci/check_package_restore.py artifacts/packages

  echo "== All 7 packages were packed"
  for package in MethodCache MethodCache.Redis MethodCache.SqlServer MethodCache.Serialization.MessagePack \
                 MethodCache.AspNetCore MethodCache.OpenTelemetry MethodCache.HttpCaching; do
    compgen -G "artifacts/packages/$package.[0-9]*.nupkg" > /dev/null || { echo "Missing $package"; exit 1; }
  done

  echo "== A release build gets the version.json version, as build/ci/publish.sh expects"
  local release_version
  release_version=$(python3 -c 'import json; print(json.load(open("version.json"))["version"])')
  rm -rf artifacts/release-version
  dotnet pack src/MethodCache.Core/MethodCache.Core.csproj --configuration Release -p:PublicRelease=true \
    --output artifacts/release-version
  [ -f "artifacts/release-version/MethodCache.$release_version.nupkg" ] || {
    echo "Expected MethodCache.$release_version.nupkg, got:"; ls artifacts/release-version; exit 1
  }

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

  echo "== Install the add-on packages and use them in a .NET 10 app"
  dotnet run --project tests/MethodCache.PackageSmokeTest.AddOns --configuration Release
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

job_diffcoverage() {
  # The net10.0 tests with coverage (the Docker-backed ones too, when Docker is there), then the share of added and
  # changed lines under src/ they ran: DIFF_COVERAGE_MIN percent (default 80) or the job fails, listing the
  # uncovered lines. DIFF_COVERAGE_BASE overrides the commit to compare with (default: merge base with origin/main).
  local results=artifacts/diff-coverage
  rm -rf "$results"
  dotnet test "$solution" --configuration Release --no-build --framework net10.0 \
    --collect "XPlat Code Coverage" --results-directory "$results"
  local reports=() report
  while IFS= read -r -d '' report; do reports+=("$report"); done \
    < <(find "$results" -name coverage.cobertura.xml -print0)
  if [ ${#reports[@]} -eq 0 ]; then
    echo "No coverage reports in $results"
    exit 1
  fi
  python3 build/ci/diff_coverage.py --min "${DIFF_COVERAGE_MIN:-80}" \
    ${DIFF_COVERAGE_BASE:+--base "$DIFF_COVERAGE_BASE"} "${reports[@]}"
}

job_properties() {
  # The CsCheck properties (tests/MethodCache.Core.Tests/Properties) from a fresh random seed, each for
  # PROPERTY_SECONDS (CsCheck_Time) instead of its fixed iteration count, on PROPERTY_FRAMEWORKS. CsCheck prints
  # the seed of a failure (CsCheck_Seed = "..."); it is repeated at the end with the command that replays it.
  local seconds="${PROPERTY_SECONDS:-5}" framework log status failed=0
  mkdir -p artifacts/properties
  for framework in ${PROPERTY_FRAMEWORKS:-net10.0}; do
    echo "== Properties on $framework from random seeds, $seconds s each"
    log="artifacts/properties/$framework.log"
    set +e
    METHODCACHE_PROPERTY_RANDOM_SEED=true CsCheck_Time="$seconds" \
      dotnet test tests/MethodCache.Core.Tests --configuration Release --no-build --framework "$framework" \
      --filter "FullyQualifiedName~MethodCache.Core.Tests.Properties" --logger "console;verbosity=normal" 2>&1 \
      | tee "$log"
    status=${PIPESTATUS[0]}
    set -e
    if [ "$status" -ne 0 ]; then
      failed=1
      echo "== A property failed on $framework. Replay it with the seed CsCheck printed:"
      # CsCheck prints it as CsCheck_Seed = "0N0XIzNsQ0O2"
      { grep -aoE 'CsCheck_Seed *= *"?[A-Za-z0-9_-]+' "$log" || true; } | sed -E 's/ *= *"?/=/' | sort -u \
        | while read -r seed; do
        echo "   $seed dotnet test tests/MethodCache.Core.Tests -c Release -f $framework --filter \"FullyQualifiedName~MethodCache.Core.Tests.Properties\""
      done
    fi
  done
  return "$failed"
}

job_soak() {
  # The soak tests (Category=Soak, skipped in every other run) under sustained concurrent load for
  # METHODCACHE_SOAK_SECONDS each; FRAMEWORK picks the target framework (default net10.0)
  METHODCACHE_SOAK_SECONDS="${METHODCACHE_SOAK_SECONDS:-30}" \
    dotnet test tests/MethodCache.Core.Tests --configuration Release --no-build --framework "${FRAMEWORK:-net10.0}" \
    --filter "Category=Soak" --logger "console;verbosity=detailed"
}

job_mutation() {
  # Stryker.NET (a local tool) mutates src/MethodCache.Core and runs tests/MethodCache.Core.Tests against each
  # mutant; the settings are in tests/MethodCache.Core.Tests/stryker-config.json. A report only: the HTML and JSON
  # reports go to artifacts/mutation/core/reports and the score never fails the job (break threshold 0).
  # MUTATION_ARGS adds Stryker options, e.g. "--concurrency 8" or "--mutate **/Runtime/Memory/*.cs" for a subset.
  local output="$PWD/artifacts/mutation/core"
  rm -rf "$output"
  dotnet tool restore
  (
    cd tests/MethodCache.Core.Tests
    set -f # a --mutate glob in MUTATION_ARGS goes to Stryker as it is
    # shellcheck disable=SC2086 # MUTATION_ARGS is a list of options
    dotnet stryker --skip-version-check --output "$output" ${MUTATION_ARGS:-}
  )
  echo "== Mutation report: $output/reports/mutation-report.html and mutation-report.json"
}

if [ $# -eq 0 ]; then
  sed -n '2,23p' "${BASH_SOURCE[0]}"
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
