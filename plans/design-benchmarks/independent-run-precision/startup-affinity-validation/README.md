# Startup-affinity validation repair

V3's first batch acquired its exclusive lease at 2026-10-15 16:36:39 UTC and finished four
benchmark cases. Its validation exited 1 at 16:39:06. The full raw archive, original error,
startup probe, metadata, guards and helpers are retained, without rewriting that exit status.
The activity guard is empty. No independent-run confidence or source-change acceptance follows.

Two issues were found in BenchmarkDotNet 0.15.4 source:

- [The affinity display](https://github.com/dotnet/BenchmarkDotNet/blob/v0.15.4/src/BenchmarkDotNet/Extensions/ProcessExtensions.cs)
  is a binary string padded to Environment.ProcessorCount. With startup affinity that count is 1,
  so mask 4 displays as `100`, not sixteen padded characters. The old parser inferred the radix
  from length and incorrectly read decimal 100. Its actual display and invalid/missing/duplicate
  cases fail the old parser tests; the repaired helper plus existing guard/statistics tests pass
  all 31 cases. Declared job affinity is explicitly distinguished from observed OS affinity.
- The same source's FixAffinity masks the requested value with `(1 << ProcessorCount) - 1`.
  With ProcessorCount=1, a request for CPU2/mask4 is masked to zero. The original log records four
  failed affinity-setting attempts. Inherited taskset affinity is plausible but was not sampled
  on those benchmark child processes; it is not claimed as independently verified evidence.

V3 remains separate diagnostic evidence. Fresh V4 retains nine fixed baseline-only launches,
16 warmups, 12 measured iterations, the same four cases/source/GC/JIT/features and bounded
exclusive leases. Only taskset sets affinity, before .NET starts; no BDN affinity characteristic
is supplied. The startup probe still requires ProcessorCount=1 and mask4. During each launch,
the monitor samples actual OS CPU sets for the owned .NET tree and requires CPU2 throughout.
It matches the four executed benchmark PIDs from BDN's exit log against those observations,
rejecting missing samples, unexpected masks, failed affinity changes and external activity.
Arguments and credentials are not recorded; no external process or host setting is changed.

The frozen one-off controller/worker/probe/helpers are in `v4-protocol/`. V4 is pending and does
not pool V1/V2/V3 data. It remains a precision study, not a product-speedup or performance pass.
