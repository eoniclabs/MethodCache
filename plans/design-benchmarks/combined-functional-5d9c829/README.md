# Exact PR137 merge-head functional CI

Commit `5d9c829a857bd377049b3bd18703e97cdb254547` passes all fourteen jobs and all four GitHub statuses, run `20261016-223405-5d9c829a857b`. The merge has parents `e39fb705` and `8002c60b` and its tree exactly matches the reviewed #137 head.

Linux: 2598 passed, no failures, one intentional opt-in soak skip across sixteen projects; 93 randomized properties, enabled soak, cultures, required Redis/SQL, demo, AOT175, all seven installed packages and docs pass. Windows/macOS build and unit jobs pass. Diff coverage is 3527/3751 (94.0%).

The manifest retains all job logs and exit-file hashes, original hashes and exact GitHub statuses. This is functional correctness evidence for this source point, not timing acceptance. Later changes to timing tools need their own CI; shipped runtime source is unchanged at `abd0eae`.
