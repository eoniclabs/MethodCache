# Acceptance inventory: cumulative timing remains open

[acceptance.json](acceptance.json) maps all76 explicit specification rows to required
scenarios, inspected assertions and exact source/report hashes. It is not a blanket
acceptance claim.

Exact published5257fb63fb72393c817d74292e989299ede374d2 passes all fourteen required
jobs and all four statuses:2,637 Linux tests,zero failures,one intentional opt-in
soak skip and94.3% changed-line coverage. This includes the complete HTTP publication
and compact-telemetry corrections, native backends, randomized properties, enabled
soak, cultures, AOT175, packages and docs.

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
A full14case cumulative candidate timing confirmation and fresh published-head CI are
still required. The existing13removal/replacementfacts remain unchanged; the new
tradeoff is a resident-entry scan on client-tag invalidation.

The proposed HTTP time bounds are inactive pending a design decision. The canonical
accepted.json still has only the two previously authorized exact no-listener sync/int
time limits. The17historical per-change cost overages remain explicit and are not
turned into passes by later combined corrections.
