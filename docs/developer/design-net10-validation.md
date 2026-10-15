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

Pending: full net10.0 functional/coverage matrix with required native Redis and SQL Server, cultures,
randomized properties, enabled soak, demo, Native AOT, installed packages, final timing and three-platform CI.
No timing acceptance is inferred from removing framework targets.
