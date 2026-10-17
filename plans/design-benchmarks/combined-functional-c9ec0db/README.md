# Exact client-index correction CI

Published `c9ec0db33d1d5b27a68ff7a31d7437c603b360a5`, run `20261017-121707-c9ec0db33d1d`: all fourteen jobs and all four statuses pass. Linux reports 2,639 passing tests, zero failures, one intentional opt-in soak skip and changed-line coverage 3620/3839 (94.3%). The separately enabled soak passes; 99 randomized property tests pass; Native AOT compiles 175 signature cases. Includes required Redis/SQL, cultures, demo, all seven packages, add-on smoke and generated docs.

The 28 raw logs/exits and original collection manifest are preserved byte-for-byte under `raw/`. This is correctness/build evidence; it establishes no timing acceptance.
