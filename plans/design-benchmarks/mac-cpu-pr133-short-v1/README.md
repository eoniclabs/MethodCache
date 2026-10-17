# PR133 short-prefix int precision reconfirmation

The original complete five-case study remains unchanged and retains its noisy
short-prefix result. This separate, predeclared ten-pair study uses all 20 fresh
launches of that one case, the exact same compiled workloads and runtime/GC,
and the same quiet-host and precision criteria. There is no cross-study pooling
or selection of individual launches.

The [strict original-rules report](timing.md) confirms +4.3%, ratio interval
1.026–1.061, with sufficient precision. Its exit 1 records the deliberate
SipHash cost before the reasoned time limits are applied. All raw launches,
readiness/measurement observations, source/preparation receipts and listener
guards are retained. The combined five-case acceptance is separate below.
