# CI scripts

MethodCache is built and tested on our own build servers, not on GitHub-hosted runners. Every CI job is a
plain script in this folder, so a job runs the same way on a build server, on your machine and anywhere else.

| Script | Runs on | What it does |
|---|---|---|
| `jobs.sh <job>...` | anything with the .NET 8, 9 and 10 SDKs | the jobs themselves: `build`, `demo`, `tests`, `unit`, `cultures`, `aot`, `packages`, `docs` |
| `linux.sh [job...]` | Linux with Docker | every Linux job inside the build image (`Dockerfile`), with Redis and SQL Server for the package apps |
| `windows.ps1 [job...]` | Windows | `build` and `unit` (the Docker-backed tests run on Linux only) |
| `Dockerfile` | | the Linux build image: .NET 10 SDK plus the .NET 8 and 9 SDKs, clang for Native AOT, Python |

Run all Linux jobs locally, the way the build server does:

```bash
build/ci/linux.sh
build/ci/linux.sh build tests   # or only some jobs
```

Each job writes `artifacts/ci/<job>.log` and `artifacts/ci/<job>.exit`. A new CI job goes into `jobs.sh` (or
`windows.ps1`) as a script or `dotnet` command, never as a step that only works in one CI system.
