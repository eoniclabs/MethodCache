# PR 4 validation: owned transports and wire contract 1

Implementation: `2c3e28121f38eeff976fe9b6f134272e656cee2c`, stacked on PR 3
`a8ad8a53814c7f3fc4e44a0bd3d2e9f2c83e6d52`. Verified locally on macOS arm64 on 2026-10-15.
The generator's frozen contract-1 vendor sources, binaries and manifests are unchanged.

## Acceptance coverage

- D01/D02/D04: two entry formats (6/7), two policy identities and invalidation from either format;
  exact keys, Unicode/reserved-looking application tags, declared method and clear. Six-minute checks prove
  the former five-minute key stamp cannot resurrect old values. Frozen wire vectors and CsCheck cover
  exact UTF-16 transport identities, operations and unknown optional fields.
- D03: malformed mandatory frames cause local clear and one payload-free warning. SQL tests use a real
  backplane table and a filled local cache; Redis callback/once-warning tests exercise the native receiver.
- D05/D06: existing delayed-write/recovery regressions remain. A delayed physical write keeps its absolute
  source-bound deadline. Native Redis/SQL tests verify atomic maximum stamps, retention that never shortens,
  scope minimum negotiation (including a node that only invalidates), 64 concurrent epoch advances,
  metadata loss, retries, full-u64 boundaries and overflow. Redis epochs have no TTL.
- D07/D08: final Core prefix, owned-service isolation, copied/frozen settings, DI connection resolution,
  invalid configuration without connecting and startup during outages. Real SQL fixtures validate version
  rows, complete column contracts, missing indexes with creation disabled, incompatible/unversioned schemas,
  and the delivered migration script against both compatible and incompatible legacy layouts.
- D09: private setters, nested Dog-in-List-of-Animal loss, an explicit polymorphic generated JSON context,
  roundtrip enabled/disabled, immutable JSON options and Distributed=false with zero backend/serializer calls.
  The roundtrip check is explicitly not a semantic equality guarantee.
- D10: retained storage/publication retry, cancellation, bounded shutdown, callback and reconnect suites.
  App cache/backplane/serializer registrations are not selected as owned transport services.
- Public docs snippets are checked semantically for use of public APIs even though their private test harness
  now has friend access to the internal backend seams. The package smoke programs also compile against
  installed packages without that friend access.

## Red-before-green evidence

Focused tests failed before the corresponding fixes for malformed-frame clearing, the five-minute key
stamp, relative physical expiry after delayed transport, Redis connection-string validation, colliding SQL
metadata table names, incompatible SQL column shapes, implicit index creation with creation disabled, and
migration of an unsupported layout. An additional analyzer regression reproduced AD0001 for array-valued
Tags before duration validation stopped reading unrelated TypedConstant.Value properties.

The SQL host-start test now advances its registered fake clock through the bounded startup wait. Test
backends use the same fake clock as absolute expiry. These are fixture corrections, not production sleeps.

## Final functional verification

- net10.0 coverage run plus net8.0/net9.0 matrices: **7,020 passed, 0 failed, 3 intentional soak skips**,
  across 46 project/framework runs. Backend tests ran with `METHODCACHE_REQUIRE_INFRA_TESTS=true`.
  Single-framework Docs.Tests runs on net10.0; the net8/9 solution filters exclude only that project.
- Changed executable source coverage: **620/638 = 97.2%** against PR 3, including newly added files.
- sv-SE and tr-TR culture jobs, random-seed properties and enabled soak: passed.
- Guided demo, Native AOT smoke and all 172 exported native signature-corpus cases: passed.
  Existing reflection-fallback AOT warnings remain; this does not claim every arbitrary result type is AOT-safe.
- All seven packages packed; base/add-on smoke tests, exact package dependencies, actual .NET 8 SDK and
  Redis/SQL-backed installed-package checks: passed.
- dotnet format, regenerated Markdown snippets/LLM docs and gitleaks: passed.

## Remaining series gates

Section 10 performance verification is still pending, including native batched L2 reads and roundtrip
on/off. It must be completed alongside the earlier PR 2/3 gates and cumulative f43854d comparison before
release verification is marked complete. No performance pass is inferred from functional tests.

The one transient initial Redis read observed under heavy PR 3 test load has not been diagnosed conclusively.
The final PR 4 backend matrices pass; that alone is not a root-cause fix. Backplane health/readiness work in
PR 5 must retain this observation and improve diagnostic evidence if it recurs.
