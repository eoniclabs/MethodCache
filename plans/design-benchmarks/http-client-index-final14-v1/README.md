# Complete client-index candidate HTTP timing

The complete fixed fourteen-case confirmation against `f43854dbcd3646893f490e78a0a1d9651e9c4521` **fails the unchanged time gate**: three slower cases and two intervals wider than 6%. Nine cases pass (seven faster, two unchanged). All 280 fresh processes and seven groups complete without execution errors. No case, pair or measurement is omitted; original and current accepted-rule reviewers both return 1.

- `DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=False)`: ratio 1.051, interval 1.045–1.058.
- Listening304 without prior invalidation: ratio 1.041, interval 1.032–1.049.
- Listening304 after invalidation: ratio 1.067, interval 1.040–1.095.
- The two cold-miss cases after invalidation have lower point estimates but intervals wider than 6%; neither is accepted as a speedup or pass.

Ten alternating pairs per case, one fresh process per case and side, 32 warmups and 12 actual BenchmarkDotNet iterations. All five growing-set miss workloads retain 8192 operations per iteration without resetting their pressure. Apple M5, SDK10.0.103/runtime.NET10.0.3, workstation GC/Interactive; same runtime/settings/workloads on both sides. Bounded reservations and quiet admission are verified. These measurements are not pooled with previous Mac/Nimbus studies.

All200baseline/224candidate production files, actual Git blobs,210binaryfiles, loaded libraries, runtime/GC, output hashes and launch order verify independently. Runtime code/build inputs match published `c9ec0db33d1d5b27a68ff7a31d7437c603b360a5`; one explicitly recorded uncompiled HTTP README differs. No performance result is inferred from CI or the earlier two-case relative pilot.

The original complete evidence directory is copied byte-for-byte under `raw/`. [Full original-rule report](raw/original-timing.md), [current-rule report](raw/accepted-timing.md), [independent validation](raw/validation.json), and all paired/individual launch records are retained. The canonical accepted rules remain unchanged; the earlier proposed HTTP bounds are inactive and do not cover every current failure or waive precision.
