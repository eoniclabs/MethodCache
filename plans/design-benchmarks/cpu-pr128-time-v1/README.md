# PR128: complete filtered CPU comparison (not a time-gate pass)

Actual PR edge `2f4a91d0c9144dd0cd9de3482291f365c4443508` →
`9c31e3b150b269d10dec99e2e93043918a718579`. Five cases, ten alternating
pairs, 100 independent processes, all frozen before launch. The pinned environment
is .NET10.0.12 / SDK10.0.401, workstation concurrent GC, Interactive latency,
CPUs0–15. Each process uses four warmups and twelve150ms actual iterations.

The [strict report](timing.md) does **not pass**: all three GUID return shapes
have intervals wider than6%. Generated sync/int short/long controls are faster.
No pair is discarded, no bound is added, and point estimates from the noisy GUID
cases are not treated as accepted or confirmed regressions. Later constructor,
SipHash and cumulative comparisons remain independent evidence.

All237 original indexed files and238 safe archive entries were independently
checked, along with exact case/pair completeness, actual measurements, runtime/GC,
actual thread affinities and quiet guards for every launch. The original archive
hash is recorded; all raw measurements and sources are byte-for-byte copies. The compiled-binary
catalogue alone uses reversible path/hash records to avoid secret-scanner false
positives on DLL filenames. Its original index and original/stored hashes remain.
`python3 restore-original-metadata.py <new-directory>` reconstructs the exact
original metadata and verifies every original indexed hash. Paired inputs retain every record. The final manifest hashes all
stored source, logs, raw measurements, analysis inputs and report files.

This is a complete per-study review. No partial means from other ongoing cohorts
were read, and their frozen workloads, sample counts and criteria remain unchanged.
