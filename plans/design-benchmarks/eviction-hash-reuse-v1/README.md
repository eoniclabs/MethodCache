# Reuse the saved node hash during eviction and expiry

A complete ten-pair comparison isolates two lines added to
`MemoryCacheEngine.OnTimerExpired` and `EvictNode`: each remembers the node's
already computed process-keyed hash before dictionary removal, as `RemoveNode`
already does. This preserves SipHash, its random key, key text, equality, features
and the generated contract. No fields or allocation layout change.

Both sides are `abd0eae840e9b277358ca643a1d4ddb3d56653cf`; **head includes the exact
retained runtime.patch**. It is not a clean Git-head comparison or cumulative
acceptance against f43854d. The baseline is the previously frozen abd build;
the candidate uses the same measured C# sources, pinned SDK/runtime, workstation
GC, affinity and launch protocol. All 222 catalogued build files were verified.

All 40 fixed launches (two cases, ten alternating pairs) pass integrity checks,
with no launches omitted or pooled. [The paired report](timing.md) passes:
Task misses improve 15.4% (ratio interval 0.832–0.861); ValueTask misses improve
14.6% (0.828–0.882). The original precise regressions provided the red performance
case. The full candidate Core suite passes 1,045 tests with zero failures and one
intentional opt-in soak skip; [test log](preparation/core-tests.log).

A separate cumulative comparison and cost profile must establish whether this
fix resolves the final gate. No acceptance limit was added for misses or GUIDs.
Only prepared.json's binary dictionary uses reversible path/hash records; run
restore-original-metadata.py with a new directory to verify all 110 original
archive file hashes before calling integrity.checked_group on that directory.
The original archive SHA-256 is
`8e05909ce636a058e65564869ab68aeb5b467b2ceb4575329f74df1e49bd1bed`.
