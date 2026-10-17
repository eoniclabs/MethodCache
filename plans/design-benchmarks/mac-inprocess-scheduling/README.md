# macOS in-process benchmark scheduling

The first quiet Mac launch produced no actual measurements: GlobalSetup attempted
to assign a QoS class after BDN's in-process executor selected a POSIX thread
priority. The full diagnostic and failed zero-measurement record are retained.
Three regression cases reproduced that failure before the fix.

Setup now asks pthread_get_qos_class_np about the actual native thread. A nonzero
QoS class permits the existing preference; an unspecified class preserves the
existing scheduling policy. Query/set errors still fail where the operation is
supported. The managed Thread.Priority enum alone is insufficient: three normal
managed-thread controls still failed with that intermediate heuristic. The native
check passes all six normal/Highest controls and all 126 benchmark cases. Each
control also verifies the thread priority was preserved. Formatting passes.

This follows [Apple's documented permanent QoS opt-out for explicit POSIX
scheduling](https://github.com/apple-oss-distributions/libpthread/blob/main/include/pthread/qos.h)
and [BDN0.15.4's explicit in-process thread
priority](https://github.com/dotnet/BenchmarkDotNet/blob/v0.15.4/src/BenchmarkDotNet/Toolchains/InProcess/Emit/InProcessEmitExecutor.cs).
No measured method, library source, sample count or acceptance criterion changes.
The fixed common sources are compiled identically on both sides of the independent
Mac confirmation. No failed setup output is pooled into its timing evidence.
Exact final published-head CI remains required.
