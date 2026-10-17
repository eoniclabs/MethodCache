# Quiet-Mac confirmation of the three precise cumulative CPU regressions

All three predeclared cases and ten pairs are retained: 60 fresh processes.
The complete [strict report](timing.md) confirms Task misses +33.3% (ratio interval
1.319–1.347) and ValueTask misses +31.5% (1.290–1.340). GUID sync hits are unchanged
on this Mac, ratio0.992 (0.965–1.020). This does not erase the original Nimbus
result; source, machine and runtime comparisons remain explicit and unpooled.

The study does not pass. No GUID/miss waiver is added. All source/preparation
hashes, actual12 measurements, runtime/GC, quiet readiness and observations were
independently verified. Both sides use the same compiled common workload and
AppleM5/SDK10.0.103/runtime10.0.3. The complete original Nimbus evidence remains
in final-paired-time-v3. Library performance corrections remain required.
