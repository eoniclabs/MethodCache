# Final timing-tool candidate functional CI

`abd0eae840e9b277358ca643a1d4ddb3d56653cf` passes all fourteen jobs and all four GitHub statuses, run `20261016-230315-abd0eae840e9`. All shipped `src/` is byte-identical to the green/profiled PR137 merge; only timing validation/export and tests changed.

Linux: 2603 passing tests across sixteen projects, no failures, one intentional opt-in soak skip. The benchmark project runs 120 tests, the benchmark Python guards 60. Real Redis/SQL, randomized properties, enabled soak, cultures, AOT175, all seven installed packages, demo and docs pass. Windows/macOS build/unit pass. Diff coverage: 3527/3751 (94.0%). Full logs/exits, original/stored hashes and GitHub statuses are in the manifest. This proves functional validation, not time acceptance.
