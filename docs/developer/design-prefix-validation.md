# Prefix-hash candidate: functional verification

Library implementation: `3aa4800`, based on PR 5 `2f4a91d0c9144dd0cd9de3482291f365c4443508`.
This is a candidate until the full section-10 timing gates pass. No 15–20 ns / 20% performance claim is made.

- F01: 64 pinned author SipHash-2-4 vectors at every byte split; CsCheck with arbitrary seeds/UTF-16 splits,
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
