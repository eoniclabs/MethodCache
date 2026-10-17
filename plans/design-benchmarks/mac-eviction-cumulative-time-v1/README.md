# Independent Mac cumulative hash-reuse confirmation

All three predeclared cases and ten alternating pairs are verified: 60 fresh
processes, the same frozen measured sources, .NET 10.0.3/SDK 10.0.103, Apple M5,
workstation concurrent GC and Interactive latency. The controller obtains the
existing coordinator reservation for at most 1,470 seconds, checks sixty quiet
seconds and records competing-work observations throughout. All 444 build files,
thirteen source files on each side and the exact two-line runtime diff verify.
Base is `f43854dbcd3646893f490e78a0a1d9651e9c4521`; head is
`abd0eae840e9b277358ca643a1d4ddb3d56653cf` plus the retained runtime.patch.

[The full report](timing.md) confirms faster Task misses (ratio 0.937, interval
0.913–0.961) and ValueTask misses (0.914, 0.898–0.931). Sync GUID hits are too
noisy (0.988, 0.925–1.055), so the complete three-case study exits 1. This does
not clear the precise Nimbus GUID regression or the full final gate. No case is
omitted and no new acceptance limit is added.

All raw measurements, source and intent/guard observations are retained. Earlier
pre-correction studies remain separate; machines and interrupted collectors are
not pooled. A first local collection stopped on a nonexistent historical
sequence-intent filename before comparison; the corrected collector uses the
actual queue/runtime patch and exit records from this independently complete run.
No measured process or raw result was changed or repeated for that collection fix.
