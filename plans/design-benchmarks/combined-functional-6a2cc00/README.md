# Exact combined-review publication CI

Published `6a2cc0020274e53f77778a8c906980423e19543d`, run `20261017-140231-6a2cc0020274`:
all fourteen required jobs and four statuses pass on Linux, macOS and Windows. Linux
reports 2,639 passing tests, zero failures, one intentional opt-in soak skip and
changed-line coverage 3615/3839 (94.2%). This includes real Redis/SQL Server, 99 randomized
properties, the separately enabled soak, both cultures, demo, Native AOT with 175
signature cases, all seven packages/add-on smoke tests and generated docs.

Runtime, build and test sources are identical to c9. All 28 raw logs/exits and the
original collection manifest remain byte-identical under `raw/`. This proves build
and correctness checks; timing acceptance remains separate.
