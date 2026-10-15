# Design implementation tracking

Goal: finish the complete pre-1.0 design in [design-spec.md](design-spec.md), including all acceptance tests,
performance gates and reviewable stacked pull requests. Authorized by Johan on 2026-10-15 to execute the
remaining series continuously. Package publication and unrelated work are outside this implementation goal.

| Step | Branch / PR | State and remaining work |
|---|---|---|
| PR 0 | codex/design-spec-v1 | Specification complete; canonical copy is included in PR 1. |
| PR 1 | codex/generated-contract-v1 / #123 | Implementation and frozen ABI fixtures complete. Preserve fixtures unchanged throughout the series. |
| PR 2 | codex/user-api-policy-v1 / #124 | Functional matrix 6,542 passed; coverage 89.7%; packages/AOT passed. Complete the broad section-10 gate, especially L2 read round trips. |
| PR 3 | codex/keys-attributes-v1 / #125 | Implemented K01–K08. Full matrix, 95.0% diff coverage, cultures, AOT/172 corpus cases and package smoke passed; Linux/Windows/macOS CI passed; section-10 performance pending. |
| PR 4 | codex/transport-format-v1 | D01–D10 implemented. 7,020 tests passed, 97.2% diff coverage; cultures/properties/soak/AOT/packages passed. Performance/external CI pending; retain the transient Redis readiness observation. |
| PR 5 | codex/core-telemetry-v1 | In progress: container-owned Core instrumentation, OTel simplification, stats and health; T01–T05. |
| Prefix hash | after PR 5 | Seeded incremental prefix hashing with reviewed collision protection; F01–F05, frozen vendor ABI and performance. |
| PR 6 | not started | HTTP Core adapter, budgets, strong epochs, cookies and observation admin; H01–H14. |
| PR 7 | not started | Full cross-platform/backend/package/AOT/docs/security and per-change/f43854d performance verification; V01–V04. |

Each implementation keeps its own isolated worktree and is stacked on the preceding implementation. CI
runs automatically on Megatron after publication; timing benchmarks must run after local builds have stopped.
Do not interrupt unrelated or active CI to obtain a quiet machine. Preserve regression, security and corpus tests;
record red-before-green evidence for bugs. Keep this file current as steps progress.
