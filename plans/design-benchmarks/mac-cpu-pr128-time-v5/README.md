# PR128: complete Mac CPU confirmation

[Strict paired report](timing.md): exit 1,
2 slower cases and 2 overly wide intervals.
A nonzero exit does not pass. No acceptance is added, no pair is omitted, and
results are not pooled with the original noisy Nimbus study.

All five predeclared cases and ten alternating pairs are present: 100 fresh
processes. Source, input/preparation hashes, exact case/side/pair membership,
twelve actual measurements per launch, .NET10.0.3 runtime, workstation concurrent
GC, Interactive latency and observation coverage were independently verified.

The Apple M5 uses SDK10.0.103 / macOS27.0.1. Both revisions run the identical
common public workloads with four warmups and twelve150ms actual iterations.
The native scheduling setup fix preserves BDN's explicit POSIX priority; it
does not change measured methods or library source and does not claim fixed
physical-core affinity.

Readiness requires60consecutivequietseconds under the recorded20percent-machine
capacity limit. Active competing build/test work rejects a launch; ordinary CPU
bursts are retained as diagnostics rather than selecting or discarding timing
rows. This follows the actual statistical gate; all prior failed readiness/setup
or extra-CPU-guard attempts remain separate and contribute no rows to this study.

All raw files, exact compiled-file catalogues, observations, common C# sources,
complete paired inputs and the reviewer result are retained with file hashes.
Published-head CI for the subsequent scheduling/test/docs changes remains separate.
