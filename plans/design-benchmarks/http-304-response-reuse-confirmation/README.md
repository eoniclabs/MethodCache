# HTTP 304 response reuse: completed three-pair confirmation

**The candidate fails the timing gate.** This fixed series compares prefix runtime
`850cef8f3bc709eaf464b3c25e5ec898217fcc73` with response-reuse runtime
`12bf99d877c33925fe0d0037f45e652d0260d8a4`. All six independent launches completed;
each of the four cases has twelve actual iterations per launch and an empty
interference guard. Launch order is prefix/candidate, candidate/prefix, prefix/candidate.
No earlier directional-pair samples are included.

| Listener / invalidation | Prefix ns (95% CI) | Candidate ns (95% CI) | Change | Assessment |
|---|---:|---:|---:|---|
| false / false | 5234.4 (5048.1–5420.6) | 5484.2 (5039.2–5929.3) | +4.77% | Insufficient precision |
| false / true | 5111.5 (4766.5–5456.6) | 5558.4 (5418.7–5698.2) | +8.74% | Insufficient precision |
| true / false | 5220.0 (4862.6–5577.4) | 5668.0 (5563.6–5772.4) | +8.58% | Insufficient precision |
| true / true | 5275.7 (5018.8–5532.6) | 5769.5 (5598.1–5940.9) | +9.36% | Regression |

Intervals use Student t with two degrees of freedom over the three independent
process means. The final row has disjoint intervals and exceeds the specification's
3% regression threshold. Other rows cannot be approved from these uncertain intervals.
Allocation falls from 8256 B to 7536 B without a listener and 7736 B with a listener,
consistently across all three launches; reduced allocation does not waive the timing gate.

The pinned image, SDK 10.0.401/runtime 10.0.12, i9-11900, concurrent workstation GC,
identical unrestricted affinity, binary checksums and workload hashes are in
`series/results/metadata.json`. Exclusive leases were released after each bounded batch;
the final controller completed at 20:20:39 UTC on 2026-10-15. `series/logs` retains actual
acquisition/release receipts. Final exit zero proves measurement completion, not acceptance.

`harness-source` contains the actual source, projects, programs and manifests retrieved
through the remote harness links, after checking their resolved paths stayed under the
task root. Symlink targets are recorded separately; absolute symlinks were not extracted.
Original and LF-normalized stored checksums are in `files-sha256.json`. Frozen helper
and workload checksums were checked before normalization. `comparison.json` was regenerated
from the reports with `validation/summarize-pair.py` and matches the original comparison.

The subsequent header-copy simplification is a new, unmeasured candidate. Its new
ownership/filtering tests pass before and after the change; all 322 HTTP tests pass after it.
It copies updated headers directly into the merged dictionary and uses a shared static
filter when no Connection options exist. No generation, budget, storage or telemetry
rule changes. This receipt neither proves its speed nor closes section 10.

The isolated local header-merge probe allocates 1248 B before and 880 B after the
simplification. Its single-process elapsed times are diagnostics only. The two new
contract cases pass on the original implementation and the replacement; they preserve
behavior rather than reproduce a functional bug. All nine changed executable lines
are covered by the 322-case HTTP run. `header-candidate` retains the probe, logs and
source digests. Full HTTP timing still requires fresh matching Nimbus launches.
