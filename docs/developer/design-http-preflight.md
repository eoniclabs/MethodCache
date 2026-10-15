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

## Fourth rejected diagnostic: 82f8ccb

Candidate `82f8ccb9fd3eaac9ecf991302756ff214bc10c91`; same base/job/runtime/workloads, one independent launch pair.
[Raw logs/manifest](../../plans/design-benchmarks/http-preflight-82f8ccb/) retain the measured state. Candidate fresh
hits off/on were 760.5/762.1 ns initially and 842.6/840.8 ns after invalidation, against 720.9/714.1 and
752.4/747.6 ns. Private variants were 1,353/1,298 ns initially and 1,446/1,436 ns afterward, against
1,267/1,262 and 1,291/1,290 ns. Allocations remain lower in all corresponding cases. Time still rejects this state.
The initial listener-off variant was noisy (83 ns standard deviation); it is not precise acceptance evidence.

A separate warmed component diagnostic (not a BDN gate) isolated URI, generation, storage and variant-key work:
old/new body lookup 36.10/101.63 ns, generation 26.71/35.13 ns, URI 176.17/140.73 ns and private key
467.25/380.88 ns. With the runtime ordinal comparer for the HTTP-only table, body lookup became 57.02 ns.
The HTTP table retains TinyLFU admission, frequency sampling, expiry, weight and full ordinal equality. Method tables
keep their prefix-aware secret hash. The runtime supplies its own string collision defenses; see the
[.NET 10 dictionary constructor](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Collections.Concurrent/src/System/Collections/Concurrent/ConcurrentDictionary.cs)
and the corresponding .NET 9 implementation. The frequency sketch retains its full process-secret hash in both modes.
Model tests cover string/span/builder/bound lookups and mutations under forced collisions and the runtime comparer;
eviction/admission outcomes match. This removes the unused HTTP process-hash priming attempt.

The completed HTTP response path now uses ValueTask internally so it creates only the Task required by HttpClient
on a synchronous hit; pending native I/O still has one observed completion/error path. Header framing uses a fixed
512-byte stack buffer and switches to incremental SHA for larger frames, keeping working memory independent of
caller header size. The exact existing UTF-16 frame is retained across that boundary. URI pooled buffers are cleared.
A dedicated Native AOT HTTP check now exercises short/large private caller keys, NoOp method-manager coexistence,
known-id invalidation, clear and source-generated enum/entry JSON without body/caller/query leakage. It passed.
The latest complete timing and functional gates remain required before accepting this repair.
