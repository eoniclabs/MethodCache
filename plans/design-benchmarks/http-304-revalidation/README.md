# HTTP 304 revalidation performance follow-up

The completed focused Nimbus comparison is a failing section-10 check before
this optimization. It compares runtime source 850cef8f3bc709eaf464b3c25e5ec898217fcc73
with 28ab9b22e02b0050a449c2bb1cfd977ec187ae25, using identical workload sources,
SDK 10.0.401/runtime 10.0.12, i9-11900, concurrent workstation GC, unrestricted
process affinity, 16 warmups and 12 measured 500 ms iterations per independent
launch. Order is prefix/change, change/prefix, prefix/change.

All four 304 cases regress by 8.4–13.1% and meet the specified disjoint-interval
and >3% stop condition. Allocations fall from 8,256 to 8,016/8,216 bytes, so the
failure is elapsed time. Generated-hit intervals remain insufficiently precise;
no generated regression or overall pass is inferred. Growing-miss rows remain
pressure diagnostics, not bounded cold-publication evidence.

The archive contains the six launch logs, complete JSON reports, source manifests
and empty interference guards. Per-report HostEnvironmentInfo records actual SDK,
runtime and CPU; the manifest's relative historical environment path is context
only, not evidence of different source runs. The first focused lease began at
12:11:35 UTC; old CI had globally finished at 12:11:43 and the first BDN log was
created at 12:12:36, after its quiet interval. New coordinator code 4b56609 began
its next pass at 12:13:01 and was verified executing at 12:18:44; later remote
fetch/checkout/collection is inside its capacity lease. These exact times are
retained rather than claiming the 12:18 observation preceded the first lease.

Foreground revalidation previously constructed one complete response/content/
header graph for storage-policy validation, disposed it, then constructed another
for the caller. It now validates the caller-owned representation before adding
caller-only diagnostic/Set-Cookie headers. The same complete merged-header and
privacy checks remain; background revalidation still owns its temporary response.
Failures dispose the unreturned foreground representation. This removes duplicate
construction; it does not diagnose every nanosecond of the original regression.

All 320 HTTP facts and 85 benchmark contracts pass in this historical HTTP
checkout on net10.0 (SDK 10.0.103, macOS arm64). Diff coverage for this optimization
is 13/13 executable changed lines. The final stack has six additional cold-miss
contracts and must run its own 91-case suite after forwarding. New comparative
timing and latest-head full CI are still required; no optimization speedup or
section-10 acceptance is claimed yet.


After forwarding to the final .NET 10 checkout, all 320 HTTP facts, 91 benchmark
contracts and 16 IHttpClientFactory/HTTP integration facts pass. The latter suite
uses in-process handlers and is not real Redis/SQL evidence. The queued Nimbus
candidate setup check uses actual owned Redis/SQL; its result is still pending.
The frozen new runtime source is 12bf99d877c33925fe0d0037f45e652d0260d8a4.
