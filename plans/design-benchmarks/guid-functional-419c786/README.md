# GUID workload checkpoint 419c786 after coordinator repair

Exact head `419c786d949d361cd2acd418178e4b0a8e21a860`, run `20261016-095858-419c786d949d`: all fourteen job exits
and all four commit statuses are success. Linux runs 2,522 facts in sixteen
projects, with zero failures and the one intentional opt-in soak skip. Required Redis/SQL,
randomized properties, enabled soak, both cultures, demo, native AOT/172 signatures,
installed packages/add-ons and docs pass. Changed-line coverage is
3467/3690 (94.0%).

`manifest.json` retains the exact SHA, run, collection time, all job exits, status snapshots,
counts and original/stored log hashes. Text is normalized to LF with trailing whitespace
removed. These are functional results for this exact head, not performance acceptance or
proof for a subsequent publication. No benchmark means are inferred from these tests.

This is a deliberate retry of the unchanged PR135 commit after the tested coordinator GC
fix was installed. No branch or code change was needed. The former macOS missing-object
checkout failure and its native Git evidence remain in `guid-macos-history-419c786`;
that failed run is not relabelled as passing. The new run finishes at 08:08:44 UTC on
2026-10-16 and independently passes macOS build/unit rather than inheriting #134 results.
