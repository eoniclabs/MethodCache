# Exact published functional checkpoint

Commit `3e9df33eb9da29c7740b2cd4434345a4486a62ce`, coordinator run
`20261017-041851-3e9df33eb9da`: all fourteen jobs exit zero and all four
GitHub statuses succeed. Linux runs 2,612 tests in sixteen projects with no
failures and one intentional opt-in soak skip; the separate soak job is enabled.
Diff coverage is 3,529/3,753 changed lines (94.0%).

This includes the saved-node-hash correction, portable evidence paths, complete
allocation-probe warmup and deterministic native deadline subscription barrier.
It confirms functional behavior, including real Redis and SQL Server, AOT,
installed packages, cultures, randomized properties, docs and demo.
It does not establish timing acceptance.

Logs are whitespace-normalized for readable review. The manifest records both
the original source-file hashes and the stored hashes, plus exact status payloads.
Earlier failed CI attempts remain in their separate evidence directories.
