# HTTP-optimized .NET 10 functional CI checkpoint

Exact runtime source: `12bf99d877c33925fe0d0037f45e652d0260d8a4`, CI run
`20261015-174658-12bf99d877c3` on 2026-10-15. This is the standalone #130 stack,
including the duplicate foreground 304-response construction repair. It does not
contain the additional native-policy test fixtures in #131 or unfinished #132/SipHash work.

The overall CI status **failed**: `docs` found the missing three generated changelog
lines in `llms-full.txt`. The failing output is retained. Regenerating that file and
forwarding it through the stack repairs the drift; newer heads still require CI.

Every other Linux job passed: build, demo, required native-backend tests, diff
coverage, randomized properties, enabled soak, cultures, AOT and installed packages.
The main test job passed **2,476 tests in 16 projects, zero failures, one intentional
opt-in soak skip**. The dedicated soak passed separately; 90 randomized properties
passed. Both culture runs passed. Windows and macOS each passed build/unit jobs.

Native AOT compiled 172 signature cases. Installed-package checks passed all seven
packages, core-first/core-last resolution, mismatched add-on rejection, actual
Redis/SQL behavior and independent-process/hash-seed reads. Cumulative changed-line
coverage was **3,462/3,686 (93.9%)**. The original job logs and per-job exits in
`manifest.json` establish these narrower claims; they do not make the failed CI
aggregate green or establish any performance gate.
