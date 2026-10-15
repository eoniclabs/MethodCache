# Completed Nimbus harness preparation and native setup

These are setup/correctness checks, **not timing acceptance**. The component controller
finished 2026-10-15 at 17:42:47 UTC and the HTTP candidate controller at 17:43:20 UTC.
Their exact process handles were absent afterward and their own terminal logs record
successful completion. Both setup batches released the exclusive capacity lease with
exit zero. Source manifests, generated programs/projects, logs and markers are retained.

## Prefix components

PR5 source `2f4a91d0c9144dd0cd9de3482291f365c4443508` and prefix source
`850cef8f3bc709eaf464b3c25e5ec898217fcc73` compile the same three workload files
from `f1da8927490a9496852947433695b8996290b6d4`; their common checksums were verified
against those Git blobs. Both projects explicitly reference DI 9.0.9. The generated
verification program is byte-identical in both harnesses.

Both pass all lookup lengths (4/80/256), prefix lengths (4/256), initial buffer sizes
(128/512), listener states and sync/Task/ValueTask facade checks. The program repeats
facade calls 100 times, requires exactly one origin load, checks key length and actual
buffer growth, and exercises the four IMemoryCache reference paths. These results close
the earlier provider-construction setup failure for these two frozen harnesses. No
component timings have run, and a future constructor/hash candidate needs its own
compatible common-source harness and verification.

## HTTP candidate native setup

Candidate source `12bf99d877c33925fe0d0037f45e652d0260d8a4` passes eight native
setup combinations: Redis/SQL Server × method round-trip off/on and HTTP cookies off/on.
Each combination executes two operations and asserts their returned values plus origin
load counts. This proves that the declared benchmark workloads exercise their intended
read/write, cached HTTP or cookie-bypass paths before timing. It is not a substitute for
the full native integration suites, independent-process contracts or L2 timing matrix.
The original log records creation and disposal of the task-owned backend containers.

Stored text uses repository LF normalization; `files-sha256.json` records stored hashes.
The SDK/runtime and CPU of the related directional timing pair remain in its separate
metadata. PR132/SipHash and section-10 acceptance are outside these checks.
