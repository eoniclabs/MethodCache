# V5 fixed full-cohort protocol: generated-program compilation required

This is a fresh cohort after V4's first generated BDN build fails before executing any
benchmark. No V4 timing samples exist to carry forward. Its full original attempt remains
on Nimbus; selected original failures and red/green proofs are published separately.

The primary workload source is `453be958abc7f57253341b8a43125404e180c551`. Only its cold-HTTP
iteration boundary changes: a void hook waits for the same asynchronous reset outside the
measured call. Component C# remains pinned at `bbff4a6dfaebfe57235f6457a62baa57170c6417`;
the project filename now matches its unchanged friend assembly name. Both harness factories
include the same real BDN partition/generate/build preflight without executing benchmarks.
Local compiled-program gates pass 67 primary cases (four are untimed growing-cache diagnostics)
and all 102 component cases. All 113 benchmark tests and 53 Python tests pass locally.

The fourteen runtime revisions, six component points, eleven comparison edges, 180 planned
launches, case inventories, ordering, warmups, iterations, SDK/runtime, GC, full-family sixteen-CPU
and component CPU2 affinities remain unchanged. Primary C# is identical across all fourteen
runtime points; component C# is identical across all six points. New prepared binary hashes
must exclude BDN-generated nested build outputs and retain the exact runtime dependency
path sets reviewed in V4. Every source, dependency and actual compiled inventory is rechecked.

Every new primary and component harness must compile its actual BDN-generated executable,
then pass setup/origin guards. Every primary runtime also rechecks native setup using fresh
owned Redis/SQL containers with before/after pinned image-ID receipts. All sixty compiled
families must pass immutable-input validation. The full dispatcher requires an additional
review and explicit launch after these gates; preparation does not establish timing acceptance.

Protocol SHA256: `7ed0602806c5a0934de83ba38b17cd4d5db9ffd75f879951c12a82b2ecb0e3d2`.
Nimbus folder: `~/methodcache-design-bench-20261015/guid-final-v5`.
Preparation PID 4036983 and validation PID 4036984 start at 08:45:33 UTC on 2026-10-16.
Both are verified live, initially waiting for capacity while current-head CI runs. This initial
preparation dispatch starts no timing controller; the subsequently completed preparation and
explicit timing dispatch are recorded in [the V5 receipt](../guid-full-preparation-v5/). Source bundle and binaries remain on Nimbus; this archive retains
exact protocol/helper bytes and checksums. Never change live frozen inputs or restart a
controller solely because it waits for a lease. Batches remain bounded to 25 minutes and
release the capacity lock between them; other jobs and services are untouched.
