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
| PR 4 | codex/transport-format-v1 / #126 | D01–D10 implemented. 7,020 tests passed, 97.2% diff coverage; cultures/properties/soak/AOT/packages passed. Linux/Windows/macOS CI passed. Section-10 performance pending; retain the transient Redis readiness observation. |
| PR 5 | codex/core-telemetry-v1 / #127 | T01–T05 implemented. 6,885 tests passed, 92.2% diff coverage; cultures/properties/soak/AOT/packages passed. Linux/Windows/macOS CI passed at 2f4a91d. Timing gates pending. |
| Prefix hash | codex/prefix-hash-v1 | Superseded by #133 (SipHash-1-3, row below). The Marvin/prepared-prefix replacement is exercised by the complete final 6c9881e descendant matrix, native corpus and independent-process package checks. Earlier 6,945-test counts belong to the retired SipHash candidate. Section-10 timing remains required; no speed claim. |
| Prefix hash, final | claude/faster-cache-hits-xft398-siphash / #133 | SipHash-1-3 with the prefix state kept in the call snapshot, stacked on #132. Johan chose it over Marvin on 2026-10-15. Section-10 against #132: GUID −2.8 %, string −5.3 %, ValueTask(int) −13 %, Sync(int) +8 %, Task(int) +5 %; see design-prefix-validation.md. |
| PR 6 | codex/http-core-store-v1 | Initial checkpoint: 7,035 passing tests and 95.9% diff coverage. Descendant 6c9881e includes shutdown/privacy/performance repairs; complete matrix 7,128 passed, 95.5% coverage of the subsequent changes, native HTTP/AOT172/packages/actualSDK8 passed. External CI and section-10 timing remain pending. |
| PR 7 | codex/design-final-gates-v1 (in progress) | Full release pipeline rerun at a711ad3: 7,143 passed, native HTTP/AOT172/packages/actualSDK8 passed; exported coordinator API regression red then green. Historical HTTP native setup and competing-Flutter guard corrected with red/green tests. Initial broad timing batch rejected, retained and being repeated with local/native separation. V01–V04 external CI/timing remain required; see design-final-validation.md. |

Each implementation keeps its own isolated worktree and is stacked on the preceding implementation. CI
runs automatically on Megatron after publication; timing benchmarks must run after local builds have stopped.
Do not interrupt unrelated or active CI to obtain a quiet machine. Preserve regression, security and corpus tests;
record red-before-green evidence for bugs. Keep this file current as steps progress.
