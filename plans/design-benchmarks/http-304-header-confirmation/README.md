# HTTP 304 header-copy candidate: completed three-pair series

All six launches complete, but all four cases have insufficient precision. No timing pass
is inferred. Baseline `850cef8f3bc709eaf464b3c25e5ec898217fcc73` and candidate
`f0dd6c204cd3eefb8fb08ef0509340caf242da92` use identical three workload files and program,
SDK 10.0.401/runtime 10.0.12, i9-11900 and concurrent workstation GC. Affinity is unrestricted
on both sides. Order is baseline/candidate, candidate/baseline, baseline/candidate.

| Parameters | Baseline ns (95% CI) | Candidate ns (95% CI) | Change | Assessment |
|---|---:|---:|---:|---|
| Listening=False&AfterInvalidation=False | 5240.5 (5057.7–5423.4) | 5354.2 (5177.3–5531.0) | +2.17% | Insufficient precision |
| Listening=False&AfterInvalidation=True | 5357.7 (5068.4–5647.0) | 5404.8 (5285.2–5524.3) | +0.88% | Insufficient precision |
| Listening=True&AfterInvalidation=False | 5129.2 (4882.7–5375.7) | 5540.9 (5089.9–5991.8) | +8.03% | Insufficient precision |
| Listening=True&AfterInvalidation=True | 5189.7 (4673.8–5705.5) | 5561.3 (5045.1–6077.6) | +7.16% | Insufficient precision |

Intervals use Student t with df=2 over the three independent process means. Each case has
twelve actual iterations per launch; every interference guard is empty. Allocation falls
from 8256 B to 7168 B without listeners and 7368 B with listeners, consistently across all
three launches. Neither reduced allocation nor overlapping intervals establishes acceptance.
The earlier response-reuse candidate's separate decisive failure remains retained.

The final controller completes at 07:09:02 UTC on 2026-10-16. `series` retains metadata,
complete reports/logs and every acquisition/release receipt. `harness-source` and the resolved
paths retain the actual build inputs, without binaries. Eighty original retrieved file hashes,
frozen helper/workload/program hashes and all six launches were checked before normalization.
`files-sha256.json` retains original/stored text hashes. `comparison.json` is rederived using
the unchanged repository `summarize-pair.py`, not a selected subset of iterations.

A fresh supplementary cohort fixes startup affinity to CPU2 and predeclares nine independent
pairs (eighteen launches), retaining all four cases and all runs. It reuses the exact same
compiled inputs, checks all 138 DLLs per side, and requires actual host/four-child OS affinity
samples. It has its own frozen protocol and results; no data is pooled with this series.
The fresh cohort has [completed all eighteen launches](../http-304-header-affinity-confirmation/),
with insufficient precision in all four cases. Whole-design timing remains separate.
