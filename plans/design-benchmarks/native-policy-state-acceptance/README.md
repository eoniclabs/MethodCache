# Native policy state acceptance

Final local runs on .NET 10.0.3, SDK 10.0.103, macOS ARM64:

| Suite | Passed | Failed | Intentional skips |
|---|---:|---:|---:|
| Core | 1033 | 0 | 1 opt-in soak |
| Redis integration | 27 | 0 | 0 |
| SQL Server integration | 63 | 0 | 0 |
| Total | 1123 | 0 | 1 |

Both integration suites require infrastructure and use fresh Testcontainers; SQL cases clean up their owned schemas.
The unchanged shared state contract contributes nine facts to each suite (27 total). Native successful operations
are forwarded to the supplied provider. A test-only async gate/observer exposes a pending read and counts value
write attempts; it stores no data and is not used in benchmarks. Existing native fault cases remain in these suites.

| Acceptance | Shared scenario / observed outcome |
|---|---|
| P03 | equivalent: reversed non-conflicting source/tag order, equal identity, actual cross-node hit, audit-only no-op |
| P04 | reload: 1h/1s policies exclude later old-policy writes, then share the new identity and real L2 after confirmation |
| P07 | specificity: attribute true/false wins over group settings and higher-priority file defaults |
| P08 | disabled + refresh: eight distinct bypass calls, held load/expired refresh join, no value-write attempts, cold-reader rejection, fresh re-enable |
| P09 | custom: async recording/NoOp managers receive immutable options, sync bypass never calls them |
| P10 | sync + unready-sync: genuine native async reuse, local-only sync caching, sync completion while actual async read stays gated, zero sync native reads |
| P13 | lost-message: connected native subscription observes/drops method frame; receiver L1/generation/policy persist until its own deadline |

The existing private-policy contract now uses the literal P02 1h shared /1s private /+2s expiry inputs.
These are verification changes only; production remains 28ab9b2 and benchmark C# workloads remain f1da892.

## Retained failures and sensitivity

The initial reload test assumed notification completion also meant native storage completion. It failed on both
providers (expected104/actual202) while the model passed. The final setup awaits the actual PolicyRegistry invalidation
task, asserts readiness/empty queues and advances the fake clock afterward. Final native output records readiness=false
before that await. This is the section-5.1 local-vs-L2 guarantee, not a claimed production fix.

An early unsubscribe-based loss fixture also failed on Redis. Its root cause is not claimed. The replacement holds
an acknowledged subscription open and drops actual frames, with a completion source confirming the method frame
was observed. A delivery-instead-of-drop mutation fails exactly the loss scenario (101 vs201); all other model cases
pass. That mutation receipt predates adding explicit value-write attempt counters, which are included in final green
runs. Invalid Groups JSON and an initial missing-using compile error were fixture construction mistakes, not red bug
proof, and are not presented as production findings.

The ordinary Core soak skip remains opt-in. These three complete suites and 27 cases do not replace the full
latest-head CI, package/AOT/culture/property/soak gates or the still-pending section-10 timing matrix.
