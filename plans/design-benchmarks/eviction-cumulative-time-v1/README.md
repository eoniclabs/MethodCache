# Cumulative node-hash reuse confirmation: one hit regression remains

All three fixed cases and ten alternating pairs are verified: 60 fresh-process
launches on Nimbus, same frozen C# workloads, .NET 10.0.12, workstation concurrent
GC, Interactive latency, pinned CPU set and interference guards. Base is
`f43854dbcd3646893f490e78a0a1d9651e9c4521`; head is
`abd0eae840e9b277358ca643a1d4ddb3d56653cf` plus the exact retained two-line
runtime.patch, not an unmodified Git revision. All 222 catalogued build files
were verified. No launches are selected, omitted or pooled with earlier cohorts.

[The complete report](timing.md) still fails on sync GUID hits: +7.7%, interval
1.050–1.105. Task misses are unchanged (0.982, interval 0.956–1.008), and
ValueTask misses improve 5.0% (0.926–0.976). All intervals satisfy precision.
Thus the miss correction works in this confirmation, while the independent hit
regression remains open. No acceptance limit is added for either shape.

The original full71, quiet-Mac and generated-prefix failures remain in their
separate proof directories. This three-case confirmation cannot clear the
entire final gate or establish a result for unmeasured scenarios. Source,
intent, every raw measurement, launch checksum and guard observation are retained.
Only prepared.json's binary dictionary uses reversible path/hash records; run
restore-original-metadata.py with a new directory to restore byte-exact metadata
and verify all 143 indexed original archive files before using integrity.py.
Archive SHA-256:
`b43cab7d6839a13c583cf1ea481c81d18da28e131769f1b413c8c8148e953e5e`.
