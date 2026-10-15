# HTTP fresh-hit diagnostic: rejected candidate 0663b331

This is one isolated BenchmarkDotNet launch per revision, not the three-run section-10 acceptance matrix.
It exposed a large allocation/time regression and therefore rejects this candidate; it establishes no speed claim.

Base: `850cef8f3bc709eaf464b3c25e5ec898217fcc73` (HTTP before the Core-store change).
Candidate: `0663b331c4621d8647db5d2183e51534b8126bff` (Core HTTP plus final-audit fixes).
Same workload source hashes, .NET 10.0.3 / SDK 10.0.103, macOS ARM64 M5, concurrent workstation GC.
Build/test launch guards were active, and this task started no concurrent builds, tests or publication during timing. Data: fresh cached response, fixed URI/body, private cache,
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

## Second rejected diagnostic: 6bf7221

Candidate `6bf7221147b3f0030433d25af9f5036d6706a446`, same runtime/base/workload/settings as above, fresh independent
launches: listener off 723.2 → 950.9 ns, on 715.8 → 944.2 ns; allocations 2.48 → 2.62 KiB. This is still rejected,
not a three-run acceptance result. [Its raw logs/manifest](../../plans/design-benchmarks/http-preflight-6bf7221/)
retain the unsuccessful intermediate repair.

The next repair prepares both the permanent-slot hash and process-key hash after the fixed hashed client prefix.
A CsCheck property proves full/split hash and slot identity with arbitrary UTF-16/surrogates and mismatched prefixes.
Default local clients keep the original empty initial namespace only while clear, URL and implicit client-tag cells
are still initial; those cells cannot return to initial after an invalidation. Custom-tag sets retain their complete
vector identity, and native L2 never uses this local shortcut. The initial-namespace regression failed before the fix;
existing gated origin/publication races and all net8/net9/net10 HTTP tests remain green. Variant framing preserves the
same exact UTF-16 bytes while avoiding temporary arrays/digest strings. Benchmark setup now explicitly proves both
fresh and private-variant hits after an unsafe mutation and origin reload, without counting origin work as a hit.
The next timings and the complete section-10 matrix remain required.

## Third rejected diagnostic: d5dd97a

Candidate `d5dd97ac1436eb8e4af31d15769ca10cd40b9f8a`, same base/runtime/job settings, now including private variants
and origin-repopulated namespaces after unsafe invalidation. All measured hits are proven not to call origin.
[Raw logs/manifest](../../plans/design-benchmarks/http-preflight-d5dd97a/) retain this one-pair diagnostic.

| Hit / listeners / after invalidation | Base ns | Candidate ns | Allocated base / candidate KiB |
|---|---:|---:|---:|
| Fresh / off / no | 718.1 | 772.6 | 2.48 / 2.22 |
| Fresh / off / yes | 769.2 | 856.9 | 2.71 / 2.62 |
| Private variant / off / no | 1,267.9 | 1,424.1 | 4.00 / 3.37 |
| Private variant / off / yes | 1,295.2 | 1,559.8 | 4.31 / 3.92 |

Every off/on hit allocates less than its corresponding base. Time still regresses and this revision remains rejected.
The listener-on private-variant initial sample was noisy (217 ns standard deviation); it is not used as precise timing
evidence. The next repair caches only an immutable options/client/storage binding, retaining each request's captured
view across config changes. Completed generation/value lookups bypass async state machines; pending native I/O is
still awaited. Variant keys hash the complete unchanged UTF-16 frame in one SHA-256 operation, using stack buffers
for small inputs and clearing pooled credential buffers on growth/return. Incremental reference tests cover empty,
large and isolated-surrogate header frames. A gated options-change regression checks that simultaneous requests
retain their own tag namespace and an earlier configuration's cache stays isolated when selected again.
