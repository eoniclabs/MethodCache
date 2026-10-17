# Approved final HTTP time acceptance

Johan explicitly chose alternative1 on2026-10-17: retain the HTTP design and activate
exactly five reasoned time caps. [decision.json](decision.json) records his instruction,
the exact prior proposal and before/after canonical rule hashes. [rules-before.json](rules-before.json)
retains the previous rules; the canonical rules are in
[accepted.json](../../../benchmarks/MethodCache.CostProfile/accepted.json).

| Exact case | Measured ratio | Approved cap |
|---|---:|---:|
| Local design HTTP miss | 1.051 | 1.06 |
| Listening 304, no prior invalidation | 1.041 | 1.06 |
| Listening 304, after invalidation | 1.067 | 1.08 |
| Cold miss after invalidation, no listeners | 1.169 | 1.20 |
| Cold miss after invalidation, listeners | 1.173 | 1.20 |

Run `python3 plans/design-benchmarks/http-time-acceptance-v1/review.py` from any
checkout location. It verifies the retained source manifests and original input hashes,
keeps every pair/actual iteration for each selected whole case, and runs the unchanged
canonical paired and cost reviewers. No benchmark or external service is started.

[coverage.json](coverage.json) covers all53existinglocalCPUcases from the original
frozen classification plus6affectedprefixmisses:59wholecases,42faster,12same,
5accepted,0unacceptedslowdowns,0wideintervals,0missing. Actual per-case study,
pairs,intervals and source provenance remain explicit. The case groups are:

- 28previously precise unaffected cases from the complete Mac53study.
- 7whole cases from the complete9case nonHTTP precision confirmation.
- 10whole cases from the current enabled-origin CPU confirmation, including2cases
  that supersede the earlier9case report and6additionalprefixmisses.
- 12whole cases from the current14case Mac HTTP confirmation.
- 2whole cases from the complete Nimbus precision follow-up.

No individual favorable pair or cross-study average is selected. The original entire
Mac14 report **still fails its two wide intervals**, even under the approved caps;
[original-mac14.md](original-mac14.md) and exit1 explicitly retain that fact. The
separate complete Nimbus2study supplies precise evidence for exactly those2cases.
Every original strict failed report remains unchanged in its original directory.
[source-impact.json](source-impact.json) and the exact diff qualify earlier precise
unaffected cases; they are not claimed to have been measured on newer binaries.

The complete67case cumulative cost replay passes:0failures,6unchangedacceptedcosts.
New time-only rules accept no bytes/commands. All correctness and6%precision rules,
all other cases and the prior2exactSipHashintcaps remain unchanged. L2 commands
remain the L2 gate; original L2 timing and new-work partitions retain their scope.

The exact previous functional checkpoint7b passes14jobs/fourstatuses. This approval
publication requires its own final CI. [inventory-before.json](inventory-before.json)
is the exact historical76row pre-approval inventory. No merge,release,PublicAPI
promotion or PR138integration is included.
