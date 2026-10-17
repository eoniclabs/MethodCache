# Complete functional checkpoint: 908a94e

Exact published head `908a94efa23fe27629ed9f00070a6bbd116740d0` passes all fourteen
CI jobs and all four commit statuses. Run: `20261016-112256-908a94efa23f`, collected
2026-10-16 09:40:21 UTC. The four statuses were independently checked again at
11:22 UTC and remain successful.

The main Linux suite passes 2,562 facts across sixteen projects, with zero failures
and one intentional opt-in soak skip. Required Redis/SQL, randomized properties,
enabled soak, both cultures, demo, AOT175, installed packages and docs pass.
Changed-line coverage is 3486/3708 (94.0%). All 53 benchmark-validation Python
tests pass. The no-listener allocation probe passes on all three platforms.

`manifest.json` records full SHA, per-job exits, exact collected logs, original and
stored hashes, commit statuses and test counts. All fourteen stored log hashes were
independently recomputed before copying this evidence into the worktree. The logs
are unchanged from the collected files; no later-head or benchmark result is inferred.

This is functional CI evidence. Section-10 timing acceptance remains open. Later
documentation/evidence commits still require their own normal CI merge gate.
