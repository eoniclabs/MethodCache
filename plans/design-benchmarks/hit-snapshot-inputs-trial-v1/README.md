# Cached snapshot-input trial: not adopted

This controlled prototype caches the immutable memory-manager reference, Enabled
value and MethodId on CallSnapshot. Binding identity, the live retirement check,
SipHash, telemetry semantics and custom-manager fallback remain intact. Both
sides include the verified node-hash reuse correction; head also includes the
exact retained prototype patch. Public signatures do not change.

All three GUID return shapes and ten alternating pairs are retained: 60 launches
with the same frozen C# workloads, runtime/GC, actual affinity and interference
checks. All 222 build-file checksums and source inputs verify. [The complete
report](timing.md) does not pass: sync is unchanged (ratio0.996,interval0.967–1.026),
while Task/ValueTask intervals are too wide. **This prototype is not applied to
the implementation.** No time bound is inferred and no original result is erased.

Only prepared.json's binary dictionary uses reversible path/hash records. Run
restore-original-metadata.py with a new directory to reconstruct byte-exact
metadata and verify all151originalarchivefilehashes before integrity.py. Archive
SHA-256: `9d1cd8623db327dd8693241896bd5008a4b1db210e955675832b5d1ac9f3eff7`.
