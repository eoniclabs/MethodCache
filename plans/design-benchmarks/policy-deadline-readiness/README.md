# Policy deadline fixture readiness

Original retained failure: Linux at f0a492ba4cc99683e9ab7c78d2194b210259dbf1, run
20261015-154832-f0a492ba4cc9. The first deadline L2 read returned the factory's 2 instead of stored1
(PolicyBackendContract line82); Windows/macOS passed. This is not labelled a production diagnosis.

The deadline/slow-factory section now owns two nodes and a fresh physical/backplane KeyPrefix, separate
from the preceding runtime-policy mutations. Native setup resolves the cache manager to start its
subscription and awaits a pass-through subscription barrier. The barrier waits for actual native
acknowledgement/initial callback completion; a socket ping is insufficient as this setup condition.
No messages or writes are discarded. Both native fixtures anchor the shared fake clock ten minutes
ahead of wall time, and all prior deadline, sliding/fail-safe, private namespace and result assertions remain.

A controlled subscription/recovery-callback fact fails if the barrier completes without awaiting the
native operation. Restored barrier + Redis contract + SQL contract pass on each of net10/net8/net9
(9 facts total, required native infrastructure, zero skips). These old framework checks apply only to
the earlier stacked HTTP snapshot; the final application target remains .NET10.

The original failure and mutation are preserved. These controlled prerequisites do not establish the
root cause of the original cache miss or claim a production fix. Latest-head CI remains required.
