# SipHash: complete functional checkpoint

Exact PR133 head `83b92c61f20bee738afd3a332e059fab44f6d8a1`, CI run
`20261015-205106-83b92c61f20b`. All ten Linux jobs and macOS/Windows build/unit jobs
pass. All three platform and aggregate statuses are success, verified on 2026-10-15.

The main Linux job passes 2,520 facts in 16 projects, zero failures and one
intentional opt-in soak skip. This includes 1,036 Core facts, all four frozen
compatibility facts, 27 Redis integration facts and 63 SQL integration facts.
Required native backends, 91 randomized properties, enabled soak, both cultures,
demo, Native AOT (172 signatures), seven installed packages and generated docs
pass. Cumulative diff coverage is 3,467/3,690 changed lines (94.0%).

The exact runtime includes the constructor and SipHash changes, plus the final
standalone allocation fixtures. This fresh macOS build/unit pass is independent
of the earlier 9338454 checkout failure; the original unreadable-tree cause
remains unproven and its failure is retained separately.

This receipt proves functional CI for the exact PR133 head. It excludes the later
component-harness repair in PR134, final GUID source freeze, timing acceptance,
lower-stack latest-head statuses and authorization to merge or release.
The manifest retains per-job exits, suite counts, original log hashes and hashes
of repository copies normalized to LF without trailing whitespace.
