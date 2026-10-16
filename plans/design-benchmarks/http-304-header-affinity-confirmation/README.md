# HTTP 304 header-copy candidate: completed nine-pair CPU2 series

All eighteen launches complete. All four cases still have insufficient precision; no timing
acceptance or speedup is established. Baseline `850cef8f3bc709eaf464b3c25e5ec898217fcc73`
and candidate `f0dd6c204cd3eefb8fb08ef0509340caf242da92` use the same compiled inputs
as the earlier three-pair study. This is a separate cohort: no earlier samples are pooled.

| Parameters | Baseline ns (95% CI) | Candidate ns (95% CI) | Point change | Assessment |
|---|---:|---:|---:|---|
| Listening=False&AfterInvalidation=False | 17039.1 (15251.5–18826.6) | 13562.0 (10427.8–16696.3) | -20.41% | Insufficient precision |
| Listening=False&AfterInvalidation=True | 17155.0 (14284.9–20025.1) | 14575.1 (11929.2–17220.9) | -15.04% | Insufficient precision |
| Listening=True&AfterInvalidation=False | 17226.5 (14957.5–19495.5) | 17139.1 (15676.0–18602.2) | -0.51% | Insufficient precision |
| Listening=True&AfterInvalidation=True | 17827.1 (16048.1–19606.1) | 14259.7 (11661.1–16858.3) | -20.01% | Insufficient precision |

Intervals use Student t with df=8 over nine independent process means. All 72 cases
retain twelve Actual Workload measurements (864 total). The primary process mean is BDN’s
`Statistics.Mean`, consistently with the frozen analysis method. BDN’s existing outlier
filter leaves N=12 in 45 cases, N=11 in 15, N=10 in 11 and N=9 in one. This does not drop
any launch; all original Actual measurements, Result measurements and statistics are retained.

Both revisions start on CPU2 before CLR startup; actual host and all four benchmark-child
affinities are verified for every launch. All interference guards are empty. Workload source,
Program and all 138 DLL hashes per revision were checked before/after workers and before each
batch. SDK 10.0.401/runtime 10.0.12 and concurrent workstation GC match on both sides.

Allocation remains lower for every candidate case (all candidate maxima below the matching
baseline minima). This does not waive timing acceptance. CPU2 times are substantially higher
than in the earlier unrestricted series; the reason is unproven. These cohorts cannot establish
an affinity effect because they ran separately. No additional adaptive timing retry is inferred.

The controller completes at 08:18:08 UTC on 2026-10-16. All 202 original retrieved file
hashes were verified before text normalization. `files-sha256.json` retains original and
stored hashes. Frozen protocol SHA256 is
`be44257b8612b19871562363a9f9cb947da7492fa8b7c62f5f64bc9ed55c66f6`.
`comparison.json` uses the unchanged `summarize-pair.py` (SHA256
`9d8f78f664d9dd06206d0ec26b576f13bf02ad45954a9c443c7129afca40bd95`).
The full GUID cohort remains separate and required.
