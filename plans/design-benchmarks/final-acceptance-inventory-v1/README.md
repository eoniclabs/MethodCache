# Acceptance inventory: cumulative timing remains open

[acceptance.json](acceptance.json) maps all76 explicit specification rows to required
scenarios, inspected assertions and exact source/report hashes. It is not a blanket
acceptance claim.

Exact publishedc9ec0db33d1d5b27a68ff7a31d7437c603b360a5 passes all fourteen required
jobs and all four statuses:2,639 Linux tests,zero failures,one intentional opt-in
soak skip and3620/3839=94.3% changed-line coverage. This includes the client-index
correction, required native backends,99 randomized properties,enabled soak,
cultures,AOT175,all seven packages/add-on smoke and generated docs.
[Original current CI logs and exits](../combined-functional-c9ec0db/).

The current cumulative cost profile passes all67cases per side with zero errors,
zero failing costs and six unchanged accepted costs. The direct-base comparison for
current PR134 also passes all67cases, with zero failures and zero accepted exceptions. Scoped F05 prefix acceptance
and the completed non-HTTP confirmations retain their actual source points.
The compact operation-scope change affects enabled origin/transport operations;
previous timing receipts must not be relabelled as measurements of that new code.
A filtered ten-case Mac confirmation measures those affected enabled-origin paths
against the baseline using the current published production sources.

Exact5257fb63 HTTP timing remains failed: the complete14case/280launch comparison
has four cold slowdowns,one listening304slowdown and one wide interval. The separate
current-source10case/200launch non-HTTP confirmation passes,all ten faster and precise.

The client-index correction is adopted in the working combined tree after all22targeted,
345HTTP and1055Core checks pass and the full67case cumulative cost profile passes with
six unchanged accepted costs. Its complete direct2case pilot is faster by7.2%/3.4%.
The full14case cumulative candidate timing confirmation is complete and fails:
3slower/2wide/9pass,all280launches without executionerrors. Actual c9 CI passes
all required jobs. [Complete current timing](../http-client-index-final14-v1/). The existing13removal/replacementfacts remain unchanged; the new
tradeoff is a resident-entry scan on client-tag invalidation.

The proposed HTTP time bounds are inactive pending a design decision. The canonical
accepted.json still has only the two previously authorized exact no-listener sync/int
time limits. The17historical per-change cost overages remain explicit and are not
turned into passes by later combined corrections.

Johan approved combined review in#134 againstmain on2026-10-17, preserving commit
history and replacing separate historical-head cost acceptance for this series.
The17old failures remain recorded. Final cumulative cost/timing and filtered CPU
requirements are unchanged; no merge or time exception is authorized.

Latest functional publication6a2cc002 passes all14jobs/fourstatuses:2639Linux/0failures,
1intentionalsoakskip,diff94.2%. The complete Nimbus2case follow-up has40launches,
2precise slowdowns16.9%/17.3%,0wide and0executionerrors. Together with the3precise
Mac slowdowns,5regressions remain. Original Mac14data and wide intervals are retained;
no studies are pooled. [Nimbus evidence](../http-precision2-nimbus-v1/),
[latest CI](../combined-functional-6a2cc00/), and the [inactive five-case proposal](../proposed-http-time-bounds-v2.json).
Canonical accepted.json is unchanged; no all76pass or final time acceptance is claimed.
