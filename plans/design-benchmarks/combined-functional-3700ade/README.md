# Combined component-capability checkpoint

Exact PR134 head `3700ade78a1450e16ad25cd90035268972e3e949`, CI run
`20261015-210236-3700ade78a14`. All ten Linux jobs and macOS/Windows build/unit jobs
pass; all three platform and aggregate statuses are success.

Linux passes 2,521 facts in 16 projects, zero failures and one intentional opt-in
soak skip: Core 1,036, Benchmarks 92, Compat 4, Redis integration 27 and SQL
integration 63. Required native backends, 91 random properties, enabled soak,
cultures, demo, AOT172, installed packages and docs pass. Cumulative diff coverage
is 3,467/3,690 (94.0%).

This includes the component-capability guard, in-place constructor and SipHash.
It excludes later workload-inventory tooling, the pending GUID source update,
latest lower-stack heads and section-10 timing. Functional green is not a timing
pass or merge/release authorization. The manifest retains original and stored
log hashes, exact exits, suite counts and status timestamps.
