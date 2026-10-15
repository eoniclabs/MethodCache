# HTTP fresh-hit diagnostic: rejected candidate 0663b331

This is one isolated BenchmarkDotNet launch per revision, not the three-run section-10 acceptance matrix.
It exposed a large allocation/time regression and therefore rejects this candidate; it establishes no speed claim.

Base: `850cef8f3bc709eaf464b3c25e5ec898217fcc73` (HTTP before the Core-store change).
Candidate: `0663b331c4621d8647db5d2183e51534b8126bff` (Core HTTP plus final-audit fixes).
Same workload source hashes, .NET 10.0.3 / SDK 10.0.103, macOS ARM64 M5, concurrent workstation GC.
No build/test/CI processes ran during these launches. Data: fresh cached response, fixed URI/body, private cache,
without/with a no-op MeterListener. Both setups prove the origin is not called by the measured hit.
16 warmups, 12 measured 500 ms iterations, one process per listener mode/revision.

| Listener | Base mean | Candidate mean | Allocated base / candidate |
|---|---:|---:|---:|
| Off | 738.3 ns | 1,435 ns | 2.48 / 4.23 KiB |
| On | 723.7 ns | 1,482 ns | 2.48 / 4.23 KiB |

[Raw logs and environment/source manifest](../../plans/design-benchmarks/http-preflight-0663/) preserve the failed
candidate. Per-launch BDN intervals are in those logs; these are not 95% intervals over three independent means.

The added zero-allocation local-vector regression test failed before the fix: 7,920,224 bytes over 10,000 captures.
The repair memoizes one immutable local generation vector per permanent slot (at most 4096), checks clear and tag
epoch references before reuse, and preserves deterministic identities across alternate tag sets. Native L2 always
reads fresh shared epochs and never uses that memo. Client identity is prepared once per handler, URL-slot lookup
uses a span, and URI hashing writes the exact frozen UTF-8 frame using stack/cleared pooled buffers and direct
string formatting. No hash is truncated and no caller/header/URI identity or generation boundary changes.

The repaired implementation still needs isolated timing and complete native/AOT/package gates. Functional tests
alone do not discharge the performance gate.
