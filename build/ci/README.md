# CI scripts

MethodCache is built and tested on our own build servers, not on GitHub-hosted runners. Every CI job is a
plain script in this folder, so a job runs the same way on a build server, on your machine and anywhere else.

| Script | Runs on | What it does |
|---|---|---|
| `jobs.sh <job>...` | anything with the .NET 8, 9 and 10 SDKs | the jobs themselves: `build`, `demo`, `webdemo`, `tests`, `diffcoverage`, `unit`, `cultures`, `aot`, `packages`, `docs`, and the longer `properties`, `soak` and `mutation` |
| `diff_coverage.py` | | the share of changed lines under `src/` that a Cobertura report covers (used by `diffcoverage`) |
| `linux.sh [job...]` | Linux with Docker | every Linux job inside the build image (`Dockerfile`), with Redis and SQL Server for the package apps |
| `windows.ps1 [job...]` | Windows | `build` and `unit` (the Docker-backed tests run on Linux only) |
| `Dockerfile` | | the Linux build image: .NET 10 SDK plus the .NET 8 and 9 SDKs, clang for Native AOT, Python |
| `coordinator.sh` | Megatron (macOS) | finds commits without a result, runs the three platforms and posts the results to GitHub |
| `install-coordinator.sh` | Megatron | checks the servers and installs the coordinator as a LaunchAgent |
| `publish.sh <tag>` | Megatron | publishes a release tag the build servers passed to GitHub Packages |

Run all Linux jobs locally, the way the build server does:

```bash
build/ci/linux.sh
build/ci/linux.sh build tests   # or only some jobs
```

Each job writes `artifacts/ci/<job>.log` and `artifacts/ci/<job>.exit`. A new CI job goes into `jobs.sh` (or
`windows.ps1`) as a script or `dotnet` command, never as a step that only works in one CI system.

## Demo verification

The `demo` job runs the eleven-chapter console tour (including the local-server HttpClient chapter) and the web demo's
cache/security/storage checks. `webdemo` runs all web-demo tests with Docker required, including two hosts
sharing Redis and an outage/restart of a private Redis container. The regular `tests` job also includes these
Redis scenarios. The `unit` and culture jobs exclude the `RedisDemo` test category along with the other
Docker-backed integration tests. Each web test uses a separate SQLite database; no developer demo data is touched.

## Diff coverage (pull requests)

`jobs.sh diffcoverage` runs the net10.0 tests with coverage (coverlet, Cobertura; the Docker-backed tests too
when Docker is there) and fails when less than 80% of the lines added or changed under `src/` since the merge
base with `origin/main` ran. It prints each uncovered changed line. `DIFF_COVERAGE_MIN` changes the threshold and
`DIFF_COVERAGE_BASE` the commit to compare with. It is in the default `linux.sh` job list, after `tests`, and
needs the full history and `origin/main` (the build servers clone both). It takes about 1.5 min
(the net10.0 test run with coverage).

## Randomized, soak and mutation checks

`properties` and `soak` are in the default `linux.sh` job list, so every pull request runs them with short settings.
`mutation` takes about an hour, so it is not: start it on a build server when you want a report
(`build/ci/linux.sh build mutation`; `build` first, because the other jobs skip themselves after a failed build). Each one is a plain entry point that runs on any machine with the SDKs (macOS
included) or in the build image (`linux.sh build <job>`). All of them expect `build` first in the same checkout,
except `mutation`, which builds for itself.

| Entry point | Run time (measured, 4 cores) | Fails on |
|---|---|---|
| `jobs.sh build properties` | about 2 min (5 s per property, 2 frameworks; raise `PROPERTY_SECONDS` for a longer search) | a property failing from a random seed |
| `jobs.sh build soak` | `METHODCACHE_SOAK_SECONDS` plus about 3 s (30 s by default) | an exception, a stampede, wrong values, inconsistent counters, the size bound or memory growth |
| `jobs.sh mutation` | about 62 min (2,251 mutants tested, concurrency 2) | Stryker itself failing, never the score |
| `jobs.sh build unit` (macOS) | 50 s on Linux with `FRAMEWORK=net10.0` (not measured on macOS) | any unit test; `FRAMEWORK=net10.0` for one framework |

- **properties**: the CsCheck properties in `tests/MethodCache.Core.Tests/Properties` with
  `METHODCACHE_PROPERTY_RANDOM_SEED=true` and `CsCheck_Time` set, so each property runs for `PROPERTY_SECONDS`
  (default 5) from a fresh seed instead of its fixed iteration count, on `PROPERTY_FRAMEWORKS` (default
  `net10.0 net8.0`). A failure prints CsCheck's `CsCheck_Seed=...`, repeated at the end of the log with the command
  that replays it. Logs: `artifacts/properties/<framework>.log`.
- **soak**: `tests/MethodCache.Core.Tests/Soak` (`Category=Soak`) runs the in-memory cache under load from every
  core for `METHODCACHE_SOAK_SECONDS` (default 30 in the job; without the variable the test is skipped).
  `FRAMEWORK` picks the target framework (default net10.0). The log ends with the operation counts, cache
  statistics and memory samples.
- **mutation**: Stryker.NET (`dotnet tool restore` installs it from `.config/dotnet-tools.json`) mutates
  `src/MethodCache.Core` and runs `tests/MethodCache.Core.Tests` against each mutant, with the settings in
  `tests/MethodCache.Core.Tests/stryker-config.json`. The HTML and JSON reports go to
  `artifacts/mutation/core/reports/` (keep them from the run folder: `linux.sh` only brings back `artifacts/ci`).
  `MUTATION_ARGS` passes more options, for example `--concurrency 8`, or `--mutate **/Runtime/Memory/*.cs` for a
  quick run on a subset.

Results and logs go to `artifacts/ci/<job>.log` and `.exit` as for every job when run through `linux.sh`.

## SQL Server integration readiness

The fixture opens a connection and runs a readiness query before cache operations start. Node setup also waits
for `SqlServerTables.Creating`, because hosted startup can return while background table creation is still
running. Tests give each distributed operation five seconds for emulated SQL Server or concurrent framework
runs; the shipped library still defaults to one second. Readiness regression tests cover pending creation,
creation errors and cancellation. Set `METHODCACHE_REQUIRE_INFRA_TESTS=true` to make unavailable infrastructure
fail the run.

## Dependency audit

Restore audits direct and transitive dependencies in every project, including test tooling. On CI, `NU1903` and
`NU1904` (high and critical advisories) are errors, so CI cannot continue with a known affected version:
`jobs.sh` and `windows.ps1` set `MethodCacheEnforceAudit=true`, which `Directory.Build.props` reads. Without it
(a local build, or a rebuild of an old tag) they are warnings, so a newly published advisory does not stop
anyone's restore. Pass `-p:MethodCacheEnforceAudit=true` to see locally what CI will do.

When CI fails on a new advisory:

1. Upgrade the package, or the direct reference that pulls it in, to a fixed version. For a transitive
   package, a direct `PackageReference` to the fixed version in the affected project works too.
2. Only when no fixed version exists or the vulnerable code cannot run in this repository, suppress that one
   advisory in the project (or in `Directory.Build.props` when every project needs it), with the link and the
   reason next to it:

   ```xml
   <ItemGroup>
     <!-- <why the advisory does not apply, or what we are waiting for> -->
     <NuGetAuditSuppress Include="https://github.com/advisories/GHSA-xxxx-xxxx-xxxx" />
   </ItemGroup>
   ```

   Remove the suppression once a fixed version is out. Testcontainers Redis and SQL Server use 4.16.0, which resolves SSH.NET 2026.0.0 for
the fixes in [GHSA-mggc-4xg6-vcxf](https://github.com/sshnet/SSH.NET/security/advisories/GHSA-mggc-4xg6-vcxf)
and [GHSA-q939-rpr3-3284](https://github.com/sshnet/SSH.NET/security/advisories/GHSA-q939-rpr3-3284).

## The build servers

The same servers as Skribera, over Tailscale:

| Server | Role |
|---|---|
| Megatron (macOS, `johan@100.118.149.97`) | coordinator; runs `jobs.sh build unit` itself with the SDKs in `/usr/local/share/dotnet` |
| Nimbus (Linux, `agent-admin@100.103.39.117`) | `linux.sh` in Docker, under `~/methodcache-ci`. It holds Skribera's capacity lease `/srv/skribera-ci/ci-build-capacity.lock` shared, so Skribera's image builds wait for it |
| baloo (Windows, `bldr@baloo`) | `windows.ps1`, under `C:\mc`. The .NET 8, 9 and 10 SDKs are installed per user in `C:\mc\dotnet` by the first run (no administrator needed) |

Every minute the coordinator looks at the head of `main` and every open pull request from this repository. Each
commit without a result runs on all three servers, one commit at a time. Each server keeps a mirror of the
repository and clones the exact commit from it, with full history for Nerdbank.GitVersioning.

Results go to GitHub as commit statuses, which are the merge gate:

- `MethodCache CI`: all platforms passed
- `MethodCache CI (Linux)`, `MethodCache CI (Windows)`, `MethodCache CI (macOS)`: one per platform

A pull request also gets a comment with the jobs per platform and the errors of a failed job. Full logs stay on
Megatron in `~/.cache/methodcache-ci/runs/<run>`, and the coordinator's own log in `~/Library/Logs/methodcache-ci.log`.

Pull requests from forks never run automatically, because their code would run on our servers. Read the diff, then
on Megatron run `~/.local/share/methodcache-ci/coordinator.sh --pr <number>`. The same command runs any pull request
again, after waiting for a pass that is already running. A run that could not reach a server is retried by itself
after 30 minutes.

### Installing or updating

On Megatron, from an up-to-date checkout of `main`:

```bash
build/ci/install-coordinator.sh --check   # what each server has and lacks
build/ci/install-coordinator.sh           # install (or update) and start
```

The coordinator is copied out of the checkout, so a pull request cannot change how it is itself tested. Settings
(server addresses, the shared lock) are in `~/.config/methodcache-ci/env`; a Linux run fails when the shared lock
cannot be opened, and an empty `METHODCACHE_CI_NIMBUS_SHARED_LOCK=` runs without it. Stop it with `--remove`.

## Publishing a release

`release.sh` (or `release.ps1`) bumps the version and pushes the tag. Once the build servers passed the tagged
commit, publish it on Megatron:

```bash
build/ci/publish.sh v1.2.0
```

It packs the 7 packages from a fresh clone of the tag and pushes them to GitHub Packages, never to nuget.org. See
`RELEASE.md` for the details.
