# Compiled workload inventory

The previous full-series script expected 46 local and 16 native cases. The
existing common GeneratedContractBenchmarks source already adds GuidHit and
StringHit, so the compiled matrix instead contains 48 local and 16 native cases.
An actual check against BenchmarkDotNet's compiled inventory failed before the
hard-coded count was replaced (`legacy-count-red.log`).

The harness now emits selected cases through `--workload-inventory FILTER...`,
using BenchmarkDotNet 0.15.4's actual parameter expansion, GlobFilter and exported
full names. Setup and measured operations do not run in this mode. The full-series
script compares inventories across all prepared revisions, freezes their names
and Program/workload checksums, and requires those names exactly once in measured
reports with twelve actual workload iterations each. Report families are derived
from the inventory. Duplicates, missing/substituted cases and warmup-only reports
are rejected.

The common sources and Program compile against f43854d and combined 3700ade. Both
produce byte-identical inventories (48 local/16 native, five/two report families)
and share the same three workload checksums and Program checksum. Six preserved
completed BDN reports, each with the ten older contract scenarios and twelve
actual samples per case, also pass the new validator against independently
filtered compiled inventories. Those historical timings are not new series data.

All 40 Python validation tests pass. The CI Python suite succeeds (47 tests run,
two existing SDK-specific skips); shell syntax checks pass. The build job now
runs the validation tests automatically. The current harness build has no warnings
or errors; the unchanged historical baseline builds with its existing NuGet audit
warnings and zero errors.

The revision list now includes separate HTTP, .NET-10-only and constructor points,
as well as the historical cumulative baseline, actual post-PR122 base, PR1–PR5,
Marvin prefix and final SipHash runtime: twelve points, three launches each and two
families give 72 planned family launches. This does not establish preparation or
timing acceptance for all twelve points; those gates remain required.

These are preliminary workloads while Claude updates the primary key arguments
to GUIDs. Rebuild and verify the same final sources on every revision before
freezing the final cohort. Inventory proves selected definitions, not cache hits;
origin/setup guards and the full section-10 review are separate requirements.
The already-running Nimbus HTTP and precision protocols were not modified.
