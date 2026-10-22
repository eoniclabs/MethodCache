# CI scripts

MethodCache is built and tested on our own build servers, not on GitHub-hosted runners. Every CI job is a
plain script in this folder, so a job runs the same way on a build server, on your machine and anywhere else.

The external coordinator remains the authoritative merge gate. The opt-in GitHub Actions pilot below
exercises the same scripts on a shared organization runner without changing existing PR validation.

## GitHub Actions pilot (2026-10-22)

GitHub Free coordinates the job; Nimbus supplies all build compute. The pilot adds one organization runner
`nimbus-org-linux-1`, label `linux-docker`, under a dedicated `eonic-ci` account. Existing Skribera runners and
the MethodCache coordinator remain running. macOS/Windows still use the external coordinator during this pilot.

MethodCache is **public**. Group `ci-public-reviewed` selects only this repository and only
`eoniclabs/MethodCache/.github/workflows/run-script.yml` at `refs/heads/ci/github-actions-pilot` during
bootstrap. After merge, add the same workflow at `refs/heads/main` to the group allowlist before manual
dispatch; GitHub validates that allowed workflows exist at their refs. Group `ci-private` remains available for separately onboarded private
projects; public repositories are not allowed there. Runner labels route jobs; groups control access.

`.github/workflows/self-hosted-pilot.yml` calls the reusable `run-script.yml`. It runs automatically only on
the named bootstrap branch `ci/github-actions-pilot`; once merged, use **Actions → Self-hosted CI pilot →
Run workflow**, selecting `main` and `smoke` or `full`. There are no PR triggers. Both caller and reusable
workflow restrict execution to trusted events/refs; no arbitrary checkout ref is accepted. A repository
writer must review code before putting it on either allowed branch. Never add fork PR triggers to this pool.
GitHub requires the workflow file on the default branch before manual dispatch is available.

`actions-linux.sh smoke` runs `linux.sh build unit`; `full` runs the existing default Linux suite. The
adapter takes an **exclusive** hold on `/srv/skribera-ci/ci-build-capacity.lock`, so it waits for existing
MethodCache/Skribera readers and image-build writers, then holds capacity until Docker cleanup completes.
A missing lock fails closed. Checkout happens before admission; heavy work happens after admission. The
inner `linux.sh` shared lock is unset to avoid reacquisition deadlocks. SDK containers are capped at four CPUs
and 16 GiB; a 30-minute admission deadline and one-hour build deadline bound each run. Timeouts are failures.
The reusable workflow has a 100-minute whole-job limit. It never cancels a running build to clear the queue.

The action token is read-only and checkout does not persist it. Logs/exits are uploaded from `artifacts/ci`
with seven-day retention, plus a job summary. Workflow actions are pinned to full commit SHAs. These results
are separate Actions checks; they do not publish or override `MethodCache CI`. Artifact storage remains
subject to the account quota; self-hosted compute has no Actions minute fee.

### Runner provisioning

`install-actions-runner.sh` is a Nimbus Linux-x64 bootstrap, not an upgrade command. Inspect it before use.
It installs runner 2.338.0 after SHA-256 verification and creates a new systemd service. Auto-update stays
enabled. The dedicated account has no sudo grant, but **Docker group membership is root-equivalent**:
this provides account separation, not a security sandbox. No production/signing credentials belong here.
Before allowing untrusted builds, introduce disposable VMs or another effective isolation boundary.

Create the restricted runner group before provisioning. Copy the reviewed installer to Nimbus, then pipe
an org registration token directly into its stdin from a trusted operator process:

```bash
set -o pipefail
scp build/ci/install-actions-runner.sh agent-admin@100.103.39.117:/tmp/methodcache-install-actions-runner.sh
gh api --method POST orgs/eoniclabs/actions/runners/registration-token --jq .token |
  ssh agent-admin@100.103.39.117 'sudo -n bash /tmp/methodcache-install-actions-runner.sh'
```

Never print or save the token. The installer refuses to replace an already configured runner. It reads
the token from stdin and uses the runner's environment input, not a token argument. After installation,
verify the org runner API and the actual pilot run; an online runner alone does not prove a successful build.

### Onboarding another project

Keep build/test commands in that project's scripts. For a trusted private project's `main` workflow, call
`eoniclabs/MethodCache/.github/workflows/run-script.yml@<reviewed-full-commit-SHA>` with `command`,
`runner-group`, `runner-label`, and `artifact-path`. Grant only that repository access to its runner group,
and provision matching capacity first. Do not grant private projects access to the public pilot group;
use `ci-private`. Until more runners are provisioned, that group has no execution capacity. For other
default branch names, explicitly extend the reusable workflow's trusted-ref gate in a reviewed change.
The first template is Linux/bash; Windows/PowerShell and macOS admission need separate validated templates.

Provisioned 2026-10-22 on the Free plan: runner group `ci-public-reviewed` (ID 4), runner
`nimbus-org-linux-1` (ID 203), home `/var/lib/eonic-ci`, runner root `/var/lib/eonic-ci/runner`.
The account has only its own primary group and `docker`; all three original Skribera services remained active.
The group was created after the bootstrap workflow was pushed, because GitHub validates workflow refs.
Trigger a fresh bootstrap push after provisioning so scheduling resolves the now-existing group.

Keep the template version pinned per caller and update it through normal review. Move shared templates
to a dedicated public `ci-platform` repository when more than this pilot is onboarded; public callers
cannot consume a reusable workflow from a private repository. Register capacity once at organization
level, rather than installing a separate runner for every project on the same machine.

### Rollback

Stop dispatching the pilot workflow. Existing coordinator checks remain authoritative, so no merge-gate
change is required. If retiring the new runner, let its job finish, then stop/remove **only**
`actions.runner.eoniclabs.nimbus-org-linux-1.service` and its GitHub registration with named authorization.
Do not restart, re-register, or remove the existing Skribera runners. Its owned home may be retired only
after checking that no build or container still uses it; never apply global Docker pruning.

## CI script reference

| Script | Runs on | What it does |
|---|---|---|
| `jobs.sh <job>...` | anything with the .NET 10 SDK | the jobs themselves: `build`, `demo`, `webdemo`, `tests`, `diffcoverage`, `unit`, `cultures`, `aot`, `packages`, `docs`, and the longer `properties`, `soak` and `mutation` |
| `diff_coverage.py` | | the share of changed lines under `src/` that a Cobertura report covers (used by `diffcoverage`) |
| `linux.sh [job...]` | Linux with Docker | every Linux job inside the build image (`Dockerfile`), with Redis and SQL Server for the package apps |
| `windows.ps1 [job...]` | Windows | `build` and `unit` (the Docker-backed tests run on Linux only) |
| `Dockerfile` | | the Linux build image: .NET 10 SDK, clang for Native AOT, Python |
| `coordinator.sh` | Megatron (macOS) | finds commits without a result, runs the three platforms and posts the results to GitHub |
| `install-coordinator.sh` | Megatron | checks the servers and installs the coordinator as a LaunchAgent |
| `coordinator-state.py` | Megatron, Python 3 | validates/reuses complete results and collects owned idle macOS checkouts |
| `storage_gc.py` | macOS, Linux, Windows | collects idle generated build output and explicitly configured disposable CI archives |
| `install-storage-gc.sh`, `install-storage-gc.ps1` | build hosts and workstations | install independent collection every 15 minutes |
| `publish.sh <tag> [--github]` | Megatron | publishes a release tag the build servers passed; nuget.org via BWS by default, GitHub Packages with `--github` |

Run all Linux jobs locally, the way the build server does:

```bash
build/ci/linux.sh
build/ci/linux.sh build tests   # or only some jobs
```

Each job writes `artifacts/ci/<job>.log` and `artifacts/ci/<job>.exit`. A new CI job goes into `jobs.sh` (or
`windows.ps1`) as a script or `dotnet` command, never as a step that only works in one CI system.

`jobs.sh build unit` is what the Windows and macOS servers run: every test except the Docker-backed ones. It takes
one .NET 10 test pass per platform. Earlier timings for the three-framework matrix are historical.

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

## Native AOT contract checks

`jobs.sh aot` publishes and executes the smoke app, including the frozen vendor DLLs. It also exports every
supported SignatureCorpus case into `artifacts/aot-corpus`, publishes that project with Native AOT, and runs
the resulting compilation check. Every scenario call remains reachable to the native compiler; normal execution
of those cases is covered by the JIT corpus and by the smoke app's supported inline key forms. Reflection-based
JSON fallback retains its documented Native AOT limitations and its compiler warnings. The exporter shares the
actual corpus definitions rather than keeping a second list of signatures.

## Randomized, soak and mutation checks

`properties` and `soak` are in the default `linux.sh` job list, so every pull request runs them with short settings.
`mutation` takes about an hour, so it is not: start it on a build server when you want a report
(`build/ci/linux.sh build mutation`). Each one is a plain entry point that runs on any machine with the SDKs (macOS
included) or in the build image (`linux.sh build <job>`). `properties` and `soak` expect `build` first in the same
checkout; `mutation` builds for itself.

| Entry point | Run time (measured, 4 cores) | Fails on |
|---|---|---|
| `jobs.sh build properties` | 5 s per property on .NET 10; raise `PROPERTY_SECONDS` for a longer search | a property failing from a random seed |
| `jobs.sh build soak` | `METHODCACHE_SOAK_SECONDS` plus about 3 s (30 s by default) | an exception, a stampede, wrong values, inconsistent counters, the size bound or memory growth |
| `jobs.sh mutation` | about 62 min (2,251 mutants tested, concurrency 2) | Stryker itself failing, never the score |

- **properties**: the CsCheck properties in `tests/MethodCache.Core.Tests/Properties` with
  `METHODCACHE_PROPERTY_RANDOM_SEED=true` and `CsCheck_Time` set, so each property runs for `PROPERTY_SECONDS`
  (default 5) from a fresh seed instead of its fixed iteration count, on `PROPERTY_FRAMEWORKS` (default
  `net10.0`). A failure prints CsCheck's `CsCheck_Seed=...`, repeated at the end of the log with the command
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

The Docker-backed integration tests and their settings are described in
[docs/developer/INTEGRATION_TEST_STRATEGY.md](../../docs/developer/INTEGRATION_TEST_STRATEGY.md).

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

   Remove the suppression once a fixed version is out.

The Testcontainers packages in the Redis and SQL Server integration tests are at 4.16.0 because it resolves
SSH.NET 2026.0.0, which has the fixes for
[GHSA-mggc-4xg6-vcxf](https://github.com/sshnet/SSH.NET/security/advisories/GHSA-mggc-4xg6-vcxf)
and [GHSA-q939-rpr3-3284](https://github.com/sshnet/SSH.NET/security/advisories/GHSA-q939-rpr3-3284).

## The build servers

Three machines on a private network, which the coordinator reaches over SSH. Their addresses, logins and the
shared lock are settings in `~/.config/methodcache-ci/env` on Megatron (see "Installing or updating" below).

| Server | Role |
|---|---|
| Megatron (macOS) | coordinator; runs `jobs.sh build unit` itself with the SDKs in `/usr/local/share/dotnet` |
| Nimbus (Linux) | `linux.sh` in Docker, under `~/methodcache-ci`. Nimbus also builds another project's images; the coordinator holds that project's capacity lock shared (`METHODCACHE_CI_NIMBUS_SHARED_LOCK`) from before checkout/collection through all jobs, so those builds wait for it |
| baloo (Windows) | `windows.ps1`, under `C:\mc`. The .NET 10 SDK is installed per user in `C:\mc\dotnet` by the first run (no administrator needed) |

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
again, after waiting for a pass that is already running. Missing results or failed status publication trigger a
retry after 30 minutes, then exponential backoff capped at six hours. Automatic attempts stop after five passes
for the commit; a deliberate `--pr` run resets that budget and reruns every platform.

The Linux pass enforces `METHODCACHE_CI_TIMEOUT_SECONDS` (default 3600) on Nimbus itself, including capacity
waiting and checkout. Its server-side `timeout` terminates the remote process group and allows 30 seconds
for `linux.sh` to clean up that run's labelled containers before a forced exit. The local SSH watchdog
allows another 60 seconds beyond the configured deadline; disconnecting SSH alone can leave Docker jobs
alive. A timed-out pass is incomplete/failed and never a passing result. Coordinator changes require
reinstallation; use `install-coordinator.sh --update-only` while an existing pass is active.

Automatic retries reuse complete passing results for the exact full commit SHA, including their logs. They
first try to recover Linux results from Nimbus when a finished job's archive did not arrive. Only platforms
without reusable results are rebuilt. Every expected job needs a valid exit file: a `finished` marker alone
does not count. Recovered test failures stay failures and end transport retries. Transfer errors are recorded
in `linux/transfer.log`.

### Checkout and log retention

The collector accepts only local macOS clones from `macos/mirror.git` with a matching run ID/full SHA and
complete macOS results. It preserves source changes, unexpected local data, unknown directories, symlinks
and checkouts with open files/processes. Successful jobs lose their generated .NET `bin`/`obj` directories
after the platform finishes; failed checkouts remain for 24 hours for diagnosis.

Remaining eligible idle checkouts expire after three days. A 10 GiB budget removes the oldest completed
eligible checkouts sooner. Active/protected checkouts and failures within their diagnostic window may exceed
the budget rather than being deleted. The independent host collector below also bounds Linux/Windows
checkouts; the coordinator's old age sweep remains a fallback. Result logs live
separately under `runs/<run>/<platform>` and survive source-clone removal; they expire 14 days after the last
report-directory update.
Successful macOS output is collected immediately when that platform returns. Full retention/budget scans
run at most once an hour, so the minute-by-minute coordinator does not repeatedly traverse all old clones.

Settings in `~/.config/methodcache-ci/env`:

| Setting | Default | Purpose |
|---|---:|---|
| `METHODCACHE_CI_KEEP_RUNS_DAYS` | 3 | completed checkout retention |
| `METHODCACHE_CI_KEEP_FAILED_HOURS` | 24 | failed macOS checkout diagnostic window |
| `METHODCACHE_CI_MAX_MACOS_MIB` | 10240 | eligible idle macOS checkout budget |
| `METHODCACHE_CI_MAX_ATTEMPTS` | 5 | automatic passes per commit |

Preview collection and run the regression tests (no GitHub/build-server access):

```bash
python3 build/ci/coordinator-state.py gc --state "$HOME/.cache/methodcache-ci" --dry-run
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s build/ci/tests -v
```

### Automatic host and local build collection

The coordinator's age sweep alone cannot prevent a busy server filling its disk in a few days.
`storage_gc.py` runs independently every 15 minutes, including when there are no new CI jobs. Linux
also collects ignored generated output immediately after a successful `linux.sh` run. Failed jobs
keep their generated output for 24 hours. Workstations use a 24-hour idle window for local output.
Immediate collection defaults to the full Linux suite. Explicit subsets such as `linux.sh build`
preserve outputs for a later `--no-build` test/demo invocation; opt in with
`METHODCACHE_CI_COLLECT_BUILD_OUTPUT=true`, or set it to `false` to keep full-suite output for inspection.

Install a separate private JSON configuration on each host. Paths must be absolute and must not
contain symlinks/junctions. Do not configure a whole home directory as a disposable archive root.
`build_roots` are **source discovery roots**: source directories are never removed there. The collector
discovers Git checkouts up to five directory levels below them and recognizes ignored/untracked .NET
`bin`/`obj`, Flutter/Gradle `build`, `.dart_tool/flutter_build`, Next.js `.next`, Node `dist`/`out`/`build`
when a build script is present, Swift `.build`, and Rust
`target` beside their project files. Source changes, locked worktrees, tracked output, database/key
files, recent writes and active processes protect output. Shared dependency caches are preserved.
Mac configurations can also list `xcode_roots` pointing to `~/Library/Developer/Xcode/DerivedData`.
Only `Build`, `Index.noindex` and `Logs` under entries with `info.plist` are removed after seven idle days
(`xcode_idle_days`). Simulator devices, signed Xcode Archives and application databases are preserved.

`archives` are **explicitly disposable CI roots**, one entry per run-directory parent. Supported kinds:

| Kind | Required ownership evidence |
|---|---|
| `methodcache` | timestamp/full-SHA prefix, clean local Git clone from configured `mirror`; `platform` is `linux`, `macos` or `windows` |
| `skribera-source` | PR/staging timestamp/SHA run name and Skribera solution file; matching metadata when present |
| `skribera-log` | PR timestamp/SHA run name and `external-ci.log` |
| `skribera-workspace` | clean `pr-<number>` Git checkout; metadata HEAD must match |
| `skribera-windows-workspace` | `v<number>-pr-<number>` snapshot with solution and matching last-run PR/SHA metadata |
| `skribera-media`, `skribera-upload` | PR timestamp/SHA run name and result/source ISO or source archive |

Each archive entry requires `root`, `kind` and `retained` (a separate log destination). MethodCache
also requires `mirror`; macOS clones use `reports` for their external results. Configure `leases`
with the workers' existing lock paths on macOS/Linux. Nimbus uses both `artifact-gc.lock` and
`ci-build-capacity.lock` for Skribera, and the capacity lock for MethodCache. Collection defers when
a worker holds a lease. Windows runs administratively and defers all roots while a build tool runs,
since its process inspection does not expose working directories.

Configuration defaults: `build_idle_hours: 24`, `failed_hours: 24`, `ci_keep_hours: 72`,
`max_ci_gib: 10` **per eligible archive root**, `log_keep_days: 14`, `max_seconds: 600`.
Pre-metadata archives get seven days unless the eligible size budget requires earlier collection.
Failed/incomplete runs get their full diagnostic window even over budget. Active, changed and
data-protected entries can exceed the budget. Internal CI archive links are removed without following
them; shared ISO/VM targets survive. VM disks, Docker volumes/images and package caches are outside
this collector. Root-level job logs and `artifacts/ci`, `ci-results` and media diagnostics are copied
to `retained` **before** a source archive is retired, then expire 14 days after collection. The collector's
own log rotates at 10 MiB with five backups. The first scan root rotates between passes, persisted in
`config.progress.json`, so a large archive cannot repeatedly consume the time budget before local or
later CI roots get their turn. The progress file is updated atomically only on apply.

Preview before installing (the default is dry-run):

```bash
python3 build/ci/storage_gc.py --config /absolute/path/host-storage-gc.json
bash build/ci/install-storage-gc.sh /absolute/path/host-storage-gc.json
```

On Windows, from an administrative PowerShell session:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/ci/install-storage-gc.ps1 -Config C:\path\host-storage-gc.json
```

The Windows installer downloads an isolated Python runtime from python.org and verifies its published
SHA-256. The task runs as SYSTEM; only administrators/SYSTEM can modify its installation/configuration.
The Unix installation is `~/.local/share/build-storage-gc`, with a macOS LaunchAgent
`com.eoniclabs.build-storage-gc` or Linux systemd `build-storage-gc.timer`. Windows uses
`C:\mc\storage-gc` and Scheduled Task `BuildStorageGc`. Check `collector.log` and the scheduler's last
exit code after installation. A failed open-file/process inspection prevents collection.

The Linux exit regression `test_successful_linux_runner_collects_output_at_exit` failed before the fix:
the old EXIT trap removed containers while leaving all generated output. Collector regressions also cover
diagnostic retention, preserved logs/data/source, tracked output, active processes, and linked shared media.

### Installing or updating

On Megatron, from an up-to-date checkout of `main`:

```bash
build/ci/install-coordinator.sh --check   # what each server has and lacks
build/ci/install-coordinator.sh           # install (or update) and start
build/ci/install-coordinator.sh --update-only  # atomic update, leave an active pass running
```

The coordinator is copied out of the checkout, so a pull request cannot change how it is itself tested. Settings
(server addresses, the shared lock) are in `~/.config/methodcache-ci/env`; a Linux run fails when the shared lock
cannot be opened, and an empty `METHODCACHE_CI_NIMBUS_SHARED_LOCK=` runs without it. Stop it with `--remove`.
The regular installer refuses to restart an active pass. `--update-only` leaves it on its original script
and atomically installs the next version; content-versioned helpers keep an older running coordinator on
its matching helper. Check `installed-from` and script hashes when diagnosing drift: committing a fix does
not update the installed copy automatically.

## Publishing a release

`publish.sh <tag>` publishes a tag the build servers passed; [docs/developer/RELEASE.md](../../docs/developer/RELEASE.md)
has the whole release procedure, including the BWS configuration and optional `--github` destination.
The `build` job also runs the Python tests under `build/ci/tests`, including simulated publishing checks
that never contact a package feed or use real secrets.

### Shared macOS and Windows runners

The cross-platform pilot adds one organization runner per host: `megatron-org-macos-1`,
`tigger-org-macos-1` and `baloo-org-windows-1`. The existing Nimbus runner remains the Linux pool.
The manual pilot runs build/unit on each Mac and Baloo, plus the selected Linux suite. Host labels
`megatron` and `tigger` prove both Macs independently; ordinary projects can request `macos-build`
and let GitHub choose an idle Mac. Windows projects request `windows-build`.

`run-script.yml` is reusable across approved `eoniclabs` repositories on `main`. It selects Bash on
POSIX and Windows PowerShell on Windows, checks out without persisted credentials, uploads separate
seven-day result artifacts per runner label and preserves the command's failure. A new project needs:

1. A reviewed repository build/test script and a caller of
   `eoniclabs/MethodCache/.github/workflows/run-script.yml@<reviewed-full-commit-SHA>`.
2. Inputs `command`, `runner-group: ci-public-reviewed`, `runner-label` and `artifact-path`.
3. An operator adding the repository ID to the group's **selected repositories**, and the exact
   reusable workflow/ref to its **selected workflows**. Keep the existing entries. Labels alone grant
   no access. No new runner process or paid GitHub plan is needed.
4. A manual run on `main`, checking the commit, physical runner, result files and uploaded artifacts.

Private repositories require an explicit trust decision before sharing this reviewed public pool.
`ci-private` remains separate and empty; it needs its own runner identity/pool before use. None of
these persistent hosts is a sandbox for unreviewed public PRs or arbitrary external collaborators.
Only push/manual events on approved refs enter the reusable workflow; forks never start automatically.

**Admission is host-wide.** On macOS the installed `host-lease.py` holds POSIX `lockf` leases on
`~/.cache/eonic-ci/capacity.lock` and Skribera's `~/.cache/skribera-external-ci/.coordinator.lock`.
This conservatively waits for the existing Skribera coordinator pass, including its child workers.
The MethodCache coordinator uses the same wrapper. Queueing is bounded to 30 minutes, execution to
60 minutes, and timed-out commands and descendants are terminated before capacity is released.
On Baloo `host-lease.ps1` opens both existing Skribera slot locks
`C:\ProgramData\bldr\ci\.lock` and `C:\ProgramData\bldr\ci1\.lock`, plus
`C:\mc\actions-capacity.lock`, with `FileShare.None`. The external MethodCache coordinator acquires
the same locks in the same order before preparing a checkout. Missing Windows slot roots fail closed.
These are cooperative build leases; they do not reserve the whole machine against interactive work.

**Installation and ownership.** `install-actions-macos.sh megatron` (or `tigger`) runs as the intended
build user and installs the official per-user LaunchAgent under `~/.local/share/eonic-ci/runner`.
It uses the existing .NET/Python installations. These Macs currently use Johan's login account:
reviewed build code has that account's host permissions, including access to its files. A separate
HOME is not account isolation. No signing, production or personal GitHub credentials are provisioned
by these installers. Use a dedicated build account/VM before admitting code with a lower trust level.

`install-actions-windows.ps1` configures the Windows runner as the existing limited user `bldr` in
`C:\Users\bldr\actions-runner`, using .NET from `C:\mc\dotnet` and Git for Windows. No administrator
grant or PowerShell 7 is needed. A per-user LaunchAgent on Megatron supervises an SSH session running
Baloo's `run.cmd`; reconnects use the existing SSH access and a 60-second restart throttle. Thus Baloo's
listener currently depends on Megatron being logged in and able to reach it. For independent reboot
recovery an administrator can replace that supervisor with the official Windows runner service under
a limited build identity. Stop only the task's supervisor/listener when idle before changing its owner.

Both installers pin runner 2.338.0 and verify the official platform SHA-256 before extraction; subsequent
runner auto-updates remain enabled. Pass a fresh organization registration token on stdin, never as a
CLI argument or a committed file. The host admission wrappers are installed outside project checkouts;
update them through the reviewed bootstrap/operations procedure. Never overwrite an existing `.runner`
configuration. Install a revised legacy coordinator with `install-coordinator.sh --update-only`; let an
already-running pass finish before starting the first cross-platform Actions build.
