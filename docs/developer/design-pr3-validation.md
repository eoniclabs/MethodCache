# Design PR 3 validation

Base: design PR 2 (`d4a150ac569b5e8e226dcad50ef2cb7c24d3dd32`), followed by its deterministic-test fixes (`e368484321cbb1c846ade78e10991e64f8cb41dc`).

## Acceptance coverage

- K01–K02: `DurationCorpus` is compiled into Core, generator and analyzer test projects. Valid inputs assert exact ticks; invalid explicit values, including sub-tick values, overflow, bare integers and zero, report errors. Generator tests run without the analyzer. Configuration errors identify their setting path.
- K03–K04: descriptor tests distinguish unset, explicit false/true and version zero, and preserve fail-safe off/retry. Existing builder and fail-safe behavior tests remain enabled. Invalid timeout/retry and explicit negative versions are generator errors.
- K05: canonical boxed scalar tests cover primitive numeric types, unsigned enums, Guid, full-precision dates, strings, providers and null. A generated read/mutation pair tests actual tag invalidation. Static text stays literal.
- K06–K07: existing reserved-key/tag, UTF-16, overload, keyed-service, closed-generic and manual-version properties remain enabled. New properties test canonical two-argument tag boundaries across Swedish and Turkish cultures. Application metadata cannot select legacy tag behavior.
- K08: retired attribute forms are compiler errors. Diagnostic IDs are contiguous MC0001–MC0021, guide links are tested, and CHANGELOG/diagnostics include the old-to-new mapping.

Frozen `CompatTests/Baselines/Contract1` files are unchanged. Old generated descriptors retain legacy tag formatting via an additive generated-contract capability, carried as internal typed data. Policy identity includes that behavior. New generators use canonical parts for both loading and invalidation.

## Red-before-green evidence

Before the fix, seven generator-only invalid-duration cases produced no errors, suffix durations failed configuration parsing, and the null dynamic tag formatted as empty text instead of the canonical null part. The corresponding tests now pass.

An initial full three-framework run passed 6,863 tests with three intentional soak skips. Subsequent full verification exercised the internal typed capability and the expanded tests, and reproduced two pre-existing scheduling races: a cancellation test using a 100 ms real delay (fixed in PR 2 with a completion barrier), and one Redis initial-read miss under concurrent build/container load. The Redis case passed its independent net8/net9/net10 rerun; investigate readiness as part of transport verification rather than treating a rerun as proof.

## Remaining verification

Run the final combined matrix after the PR 2 test fixes, coverage, cultures, AOT/signature corpus, package smoke tests and the section-10 benchmark gate. Record results below before the series is considered complete.

## Combined implementation verification

Source revision: `0b0460b` (the typed capability and PR 2 test barriers are included).

- Full Release matrix: 6,872 passed, zero failed, three intentional soak skips, 46 project/framework runs. Real Redis and SQL Server integration fixtures ran.
- Changed-source coverage against PR 2: 207/218 lines (95.0%). Shared duration parser: 40/40 changed lines.
- sv-SE and tr-TR unit runs passed.
- Guided demo: all six chapters passed.
- Native osx-arm64 smoke passed; 172 signature corpus cases compiled and executed natively. Existing reflection-fallback trimming/AOT warnings remain and do not imply arbitrary fallback types are AOT-safe.
- Seven packages packed; Core and six add-ons smoke-tested, including actual .NET 8 SDK compiler consumption and real Redis/SQL Server.
- Frozen contract-1 binary fixtures passed unchanged. Snippets and LLM documentation regenerated; formatter ran.

Section-10 benchmark comparisons and external cross-platform CI remain recorded as outstanding verification in the execution tracker. PR 4 also follows up the observed transient Redis startup read; the combined full matrix passed, which does not by itself prove that scheduling issue resolved.
