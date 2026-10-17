# Exact published benchmark-tool fix: complete functional CI

Head `453be958abc7f57253341b8a43125404e180c551`, run
`20261016-104239-453be958abc7`, passes all fourteen Linux/macOS/Windows jobs and all
four commit statuses. The exact run commit, fourteen exit codes, complete logs and original/
stored hashes were checked. Linux runs 2,562 facts across sixteen projects, zero failures,
and one intentional opt-in soak skip; Core 1039, Benchmarks 113, Redis integration 33,
SQL integration 63, generator integration 542, generator unit 66 and Docs 119.

All 53 benchmark-validation Python tests pass on Nimbus. The separate fifty CI-operation
Python tests retain their two existing opt-in publishing-tool skips. Required native
backends, randomized properties, enabled soak, both cultures, demo, AOT175, installed
packages/add-ons and docs pass. Changed-line coverage is 3486/3708 = 94.0%.
The strict zero-allocation telemetry probe passes on every platform; the old allocation
source remains unproven. This is functional evidence, not section-10 timing acceptance.
Subsequent evidence-only publication needs its own CI; no ancestor pass is inherited.
