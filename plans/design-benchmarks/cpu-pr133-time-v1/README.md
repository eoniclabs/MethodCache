# PR133: complete filtered CPU comparison

The [strict report](timing.md) retains all five predeclared cases and ten alternating
pairs per case. Review exit is 1, with 3 overly wide intervals.
This does not pass when the exit is nonzero. No noisy or inconvenient pair is
discarded, no acceptance limit changes, and point estimates from wide intervals
are not treated as confirmed or accepted regressions.

All 238 safe archive entries, 237 indexed file hashes and 100 exact launches were
independently verified, including twelve actual measurements, runtime/GC, actual
thread affinity and quiet guards for every launch. Pinned .NET10.0.12 / SDK10.0.401,
workstation concurrent GC, Interactive latency, CPUs0–15, four warmups and twelve
150ms actual iterations are the same on both sides.

Original raw measurement and source bytes are unchanged. The compiled binary
catalogue alone uses reversible path/hash records to avoid secret-scanner false
positives on DLL labels. Original/stored hashes are retained. Run
python3 restore-original-metadata.py <new-directory> to reconstruct byte-exact
original metadata and verify the original index before reusing the raw study.
The original archive hash and every stored file hash are in the manifest.

This is a complete independent study. No partial means from ongoing comparisons
are used; their frozen sources, counts and criteria stay unchanged. Separate Mac
confirmation is separate evidence, with no cross-machine pooling.
