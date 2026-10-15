# Unresolved local-admin observation in historical package CI

The historical #129 runtime `2bb7c0423a800ec43cfd4ab270682d6a6bc2169a` failed its Linux
package job in the net8.0 add-on app: SQL Server seed-isolation phase 2 (`hit`) exited 5.
The original output is retained. That exit occurs **after** the expected value and zero origin
calls have been checked, when the expected synthetic key is absent from `ListEntries()`.
It is not evidence of an L2 miss. The original worker discards its child output, so the local
entry count, actual keys and concurrent invalidations are unknown. No root cause is claimed.

A separate external net10.0 diagnostic app retained every assertion and exit code, adding
synthetic-key diagnostics only on failure. It used the existing installed packages at
`1.0.0-g28ab9b22e0`, with full package SHA256s and source checksums recorded in the manifest.
Core shipped sources at that package revision and runtime `12bf99d877c33925fe0d0037f45e652d0260d8a4`
are identical; the later change is in HTTP caching. This is diagnostic evidence, not a current
whole-package revalidation. The external driver owned and disposed its labelled Redis/SQL
containers; credentials were passed only through the child environment.

Three fixed SQL seed-isolation rounds (nine independent process phases each, 27 total) pass on
macOS ARM64/.NET 10. No failing assertion was removed, retried to success or given a tolerance.
These passing runs do not explain the original Linux/net8 observation or prove it cannot occur
on .NET 10. The supported standalone net10 runtime's earlier exact-source CI package checks at
12bf99d also pass, as retained separately. Newer-head CI remains required; production code and
assertions are unchanged, and the historical failure remains unresolved.

`diagnostic.patch` preserves the only app-source changes against the recorded repository source.
The manifest also pins the modified source/project checksums and exact driver invocation.
