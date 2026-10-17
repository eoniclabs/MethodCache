# Frozen full-design timing protocol, version 4

These are the exact deployed preparation/measurement/analysis sources. Preparation remains live;
no complete timing series is claimed. Existing twelve-point preparation and its two-point overlay are
reused; the fourteen exact runtime SHAs and eleven actual PR/cumulative edges are in the JSON files.
Primary workload source is B925; the four-file genuine generated component workload is bbff4a6.
Both are available in Git. The original source bundle is retained in the task archive, not duplicated here.
A reconstructed future attempt must record its own bundle/binary hashes rather than claiming the old pack.

Scope: fourteen primary points × three families × three independent rounds (126 launches), and six
component points × three families × three rounds (54 launches). The 180 launches remain fixed. Component
families are disjoint (18 lookup, 36 facade/reference, 48 genuine generated). Int/Guid stay separately labelled.
Sixty compiled family inventories must pass input validation before a measurement directory can be created.
The validation controller waits for the already-running preparation controller; it does not dispatch timing.

Use the pinned SDK image, existing capacity lease (exclusive nonblocking, at most 25 minutes per batch),
identical raw C# and harness program hashes, actual binary hashes and observed OS affinity. Main workloads
retain CPUs 0–15 for contention; component workloads bind CPU2 before CLR startup. Every measured case needs
12 actual iterations and concurrent workstation GC; each independent launch has 16 warmups and 500 ms iteration
time. Native launches use digest-pinned Redis/SQL images and check actual owned container image IDs before/after.

The timing worker's validate-only mode works before a timing-results directory exists. The complete 60-family
validation receipt pins the prepared manifest and protocol. Full timing is started explicitly only after
that receipt is reviewed. Deployed files/prepared inputs are immutable; preserve every genuine failure.
The summary requires all 180 unique successful tuples and actual comparison edges. Its 95% t intervals use
three independent launch means, not within-process BDN iterations. It is not automatic acceptance: inspect
feature equivalence, new work, uncertain intervals, decisive regressions and corresponding hit allocations.

Run the archived Python guard tests with PYTHONDONTWRITEBYTECODE=1; interpreter caches are not protocol inputs.
