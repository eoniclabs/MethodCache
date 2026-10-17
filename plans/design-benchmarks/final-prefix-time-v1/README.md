# Complete final generated-prefix time comparison: does not pass

All 28 predeclared cases and ten pairs are retained: 560 verified fresh launches.
The [complete report](accepted-timing.md) has eleven precise miss slowdowns and
two overly wide intervals. No individual run is omitted or pooled with other
studies. Most generated hits pass; both prefix lengths and listener states show
miss regressions. The two precise sync/int limits do not waive GUID/miss costs.

All raw launches, actual12 measurements, .NET10.0.3 runtime/workstation GC,
quiet readiness, observation coverage, source/input/preparation receipts and the
original/accepted-rule reports are retained. The independent 48-case cost phase
passes, and explicit JIT/NativeAOT correctness passes; those do not establish time
acceptance. A correction is being measured separately. This evidence remains the
unchanged pre-correction result, not a passing final gate.
