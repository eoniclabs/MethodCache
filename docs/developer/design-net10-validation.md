# .NET 10 minimum (2026-10-15)

Johan approved removing .NET 8 and .NET 9 before 1.0. All seven runtime packages, normal tests, package apps
and Native AOT programs now target net10.0. The SDK baseline is 10.0.100 with latestFeature roll-forward
within .NET 10. Roslyn generator/analyzer/code-fix assemblies retain their required netstandard2.0 tool
host format; that is not an additional supported application runtime.

The Linux CI image contains only SDK/runtime 10, Windows provisioning requests only SDK 10, and the
installer accepts an SDK-10-only machine. Existing shared-host SDKs are not uninstalled. Package restores
and randomized properties use only net10.0. The old allocating string fallback and framework-version
preprocessor branches are removed from the runtime and newly generated decorators.

Frozen Contract1 source, DLLs and hashes remain byte-for-byte unchanged. Their original net8 build is
provenance, not an application target we still ship or test. The explicit freeze utility belongs to that
historical baseline and is not part of the current build or CI. Historical release/benchmark receipts
retain their original framework and SDK details. C# 12 source language coverage remains on .NET 10.

Initial checks: Release solution build, 66 generator tests, 533 generator integration cases and the CI
script suite (45 tests, two existing optional publish-SDK skips) pass. Snapshot differences contain only
removal of the old conditional fallback. New package and SDK-installer acceptance tests failed before
the implementation and pass after it. Selected logs: `plans/design-benchmarks/net10-only/`.

## Full functional receipt

At `28ab9b22e02b0050a449c2bb1cfd977ec187ae25`, the complete release pipeline passed on macOS ARM64,
SDK 10.0.103 / runtime 10.0.3, with required real Redis and SQL Server in task-owned containers. Source
was unchanged during the run. **2,475 tests passed, zero failed, one intentional opt-in soak skip,
16 project runs**. The separately enabled soak passed, as did tr-TR/sv-SE and all 90 randomized properties.

Diff coverage for the net10-only PR has no added executable C# lines; its five changed lines are comments.
For the HTTP branch, coverage is **733/766 = 95.7%**; cumulative since the PR-1 merge base it is
**3,451/3,675 = 93.9%**. Both exceed the required 80%.

Demo, Native AOT HTTP/private variants and HTTP-only administration, all **172 native signature cases**,
and all **seven installed packages** pass. Package checks confirm net10.0-only runtime assets and
dependency groups, exact Core version bounds and rejected mismatches. Redis/SQL independent-process
and hash-seed phases pass, along with JSON/MessagePack, ASP.NET Core, OpenTelemetry and HTTP apps.
The known reflection-fallback AOT warning categories remain; this is not a zero-warning claim.

[Selected full receipts](../../plans/design-benchmarks/net10-functional-28ab9b2/) preserve this run.
The net10 runtime lookup code retains the former NET9_0_OR_GREATER branch unchanged; new generated code
removes only its inactive conditional/string fallback for net10 consumers. There is no claim that
removing framework targets improves runtime throughput.

Pending: final section-10 timing and three-platform CI. The approved Nimbus series now freezes this
net10 candidate instead of the superseded 8063e9a candidate. Only unchanged revision/workload evidence
can be reused. Earlier rejected/superseded reports remain separate.
