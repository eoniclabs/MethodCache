# CI scripts

MethodCache is built and tested on our own build servers, not on GitHub-hosted runners. Every CI job is a
plain script in this folder, so a job runs the same way on a build server, on your machine and anywhere else.

| Script | Runs on | What it does |
|---|---|---|
| `jobs.sh <job>...` | anything with the .NET 8, 9 and 10 SDKs | the jobs themselves: `build`, `demo`, `tests`, `unit`, `cultures`, `aot`, `packages`, `docs` |
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
