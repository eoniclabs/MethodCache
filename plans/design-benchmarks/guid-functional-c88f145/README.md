# GUID workload functional checkpoint c88f145

Exact source: `c88f145e0009b6416aa460a6e7015342f86412e2`.
All fourteen required jobs exit zero: ten Linux jobs and macOS/Windows build and unit jobs.
All four commit statuses are success. Linux main tests pass 2528 facts across sixteen
projects with zero failures and one intentional opt-in soak skip. This includes 1036 Core
facts, 99 benchmark contracts and real Redis/SQL integration suites (27/63 facts).

The 91 randomized properties, enabled soak, both cultures, demo, 172 Native AOT signature
cases, all seven packages plus installed-package/add-on smoke tests and docs pass.
Cumulative changed-line coverage is 3468/3690 (94.0%). Original and LF-normalized log
checksums, exact job exits, individual suites and observed statuses are in `manifest.json`.

This validates the GUID origin guards, compiled-inventory tooling and controlled
synchronization-context fixture at this exact checkpoint. It precedes the HTTP
header-copy optimization f0dd6c2 and combined source b92553d. Those newer heads need
fresh CI. No section-10 timing pass, native benchmark setup or GUID timing is inferred.
