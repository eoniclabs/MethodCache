# Complete final paired-time comparison: gate remains open

All 71 predeclared cases and ten alternating pairs are present: 1,420 verified
formal launches with twelve actual measurements, matching runtime/GC and actual
thread-affinity/interference guards. Original complete groups, rejected group01,
the complete reconfirmation and corrected listener controls, and the six native
continuation groups are all retained. The native continuation fixes only a missing
Docker CLI before any L2 sample; it uses the identical frozen compiled workloads.

The [full accepted-rule report](all-accepted-timing.md) does not pass: eight precise
slowdowns and 45 overly wide intervals. The [53 existing local CPU cases](local-cpu-accepted-timing.md)
have three precise slowdowns and 32 wide intervals. In particular generated GUID
sync hits rise 8.9%, Task misses 10.6%, and ValueTask misses 11.8%. These cases
receive no waiver and require investigation/quiet confirmation and correction.

The original and accepted-rule reports are both retained for every partition.
The two authorized sync/int limits do not accept GUID/miss regressions or waive
precision. The [source-based partition](l2-gate-classification.json), frozen before
final means, preserves all 71 rows while separately reporting ten existing L2
cases and eight new-work cases. The specification makes command counts the L2
gate; its independent cost profile is separate from this latency report. A local
subgroup pass must never be presented as a pass of the strict all-case report.

Only the four prepared binary-catalogue dictionaries use reversible path/hash
records. All original/stored checksums are retained. Run restore-original-metadata.py
with a new output directory to reconstruct byte-exact original metadata and verify
all four archive indexes before reuse. No executables or source bundles are included.

The original supplementary container named `aux/` is stored as `additional/`:
Windows reserves AUX as a path component, so the earlier name prevented checkout.
All 375 moved files are byte-identical. Original provenance labels inside retained
JSON remain intact and the manifest records the path mapping. The restorer uses
`additional/` on every platform and still verifies all four original archive
indexes; each archive's contents and metadata remain byte-exact.
