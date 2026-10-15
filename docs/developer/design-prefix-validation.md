# Prefix-hash candidate: functional verification

Initial SipHash library implementation: `3aa4800`, based on PR 5 `2f4a91d0c9144dd0cd9de3482291f365c4443508`.
This is a candidate until the full section-10 timing gates pass. No 15–20 ns / 20% performance claim is made.

- F01 for the initial implementation: 64 pinned author SipHash-2-4 vectors at every byte split; CsCheck with arbitrary seeds/UTF-16 splits,
  isolated surrogates, escaping, empty parts, buffer growth, fallback keys and Append after lookup.
  Finalization copies state; snapshot prefix data never changes.
- F02: forced collisions across string/span/builder insert/read/replace/remove, a dictionary model, concurrent
  publications, and identical frequency admission/eviction outcomes. Full ordinal text always determines equality.
- F03: installed-package workers start independent CLRs and therefore independent process hash secrets.
  Nine worker phases per Redis/SQL test cover population, cross-process L2 hit, helper-key removal, tag removal,
  clear and restart hit. Public admin key text must match MethodCacheKeys.For. No process hash/seed is exported.
- F04: [algorithm/threat review](prefix-hash-security.md), author reference vectors, known seeded-polynomial
  collision families with long common prefixes/NUL/surrogates, and bounded hash-chain comparison work rather
  than fragile timing assertions. Distribution tests alone are not treated as a security proof.
- F05 functional portion: frozen contract-1 vendors unchanged; net8/9/10 matrix, Native AOT and native corpus pass.
  Dedicated full-path and component benchmark workloads validate their setup/origin counts before timing.

Functional matrix: **6,945 passed, 0 failed, 3 intentional soak skips** over 46 project/framework runs, native
backends required. Changed executable source coverage: **74/81 = 91.4%** against PR 5. Cultures, random properties
on net10/net8, enabled soak, demo, Native AOT smoke and all 172 native signature cases passed. All seven packages,
base/add-on smoke apps on net8/net10 and actual .NET 8 SDK checks passed, including independent-process workers.

The first process test failed only on its final hit after clear. Direct inspection showed the initial cross-process
hit and shared namespace were correct; the configured skew allowance intentionally rejected a publication made
immediately after invalidation. The colocated-process identity fixture now explicitly uses zero ClockSkewTolerance,
and all phases pass on Redis and SQL. It adds no sleeps and does not weaken production invalidation rules.

The full-path/component, listener, L2 and cumulative timing gates remain pending; functional green is not timing
evidence. Existing broader PR 2/3/4/5 performance obligations remain in the execution tracker.

## Current replacement candidate

The initial SipHash timing gate failed; see [the recorded measurements](design-prefix-preflight.md).
The current replacement uses the .NET randomized Marvin algorithm, independently checked against a pinned
v10.0.0 reference oracle. UTF-16 suffix finalization, all byte splits, immutable string memo interleaving,
forced collisions and append/growth are covered. Its security basis and limits are described in the
[updated review](prefix-hash-security.md). The 6,945-test/full-package evidence above belongs to the initial
implementation and must not be attributed to the replacement until rerun. A default-builder regression
first failed (empty-string hash memo contamination) and was corrected before the replacement was committed.
The current `850cef8` replacement is also included in the final `6c9881e` descendant's complete net8/9/10 matrix,
random properties, real Redis/SQL tests, native smoke/172-case corpus and installed-package checks, including
independent-process hash-seed phases. See [the functional receipt](design-final-validation.md) for exact source and
counts; those counts cover the complete descendant, not a standalone prefix-only run. Timing remains pending.


## Standalone replacement verification

The complete release pipeline was also rerun on the standalone `850cef8f3bc709eaf464b3c25e5ec898217fcc73`
production source, rather than relying only on its HTTP descendant. It passed **6,957 tests, 0 failures and 3 intentional
soak skips across 46 project/framework runs** (net10: 2,400; net8: 2,276; net9: 2,281), with required real Redis and
SQL Server. Changed executable source coverage against PR 5 is **149/157 = 94.9%**. Cultures, random-seeded properties
on net10/net8, enabled soak, demo, Native AOT smoke and all 172 native signature cases passed. All seven packages,
matching/mismatched version checks, actual SDK 8.0.203 and independent Redis/SQL CLR/hash-seed phases passed.

The first attempt reproduced the known allocation-probe problem already fixed in the HTTP branch: workers were
measured before their own first-use warm-up, producing 48 extra baseline bytes on three .NET 8 workers while the
listener-enabled sample was lower. The same worker warm-up fix is now included here. The allocation equality
assertion remains unchanged, and all 44,000 listener measurements, including warm-up, must be preserved. Three
allocation cases passed before the full pipeline was repeated successfully. The first failed run is retained and
is not counted as passing evidence. Regenerating LLM documentation also corrects stale SipHash wording.

[Selected standalone receipts](../../plans/design-benchmarks/prefix-functional-850cef8/) record both attempts and
the exact production source. This test/documentation-only follow-up does not change the library being measured on
Nimbus. Section-10 timing and external CI remain required; no speed claim is introduced.
