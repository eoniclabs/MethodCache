# PR134 review corrections

Review: https://github.com/eoniclabs/MethodCache/pull/134#pullrequestreview-5558329141
The previous completion claim is superseded by this review. Corrections remain local until
all comments and the independent review of the complete PR are finished. No new CI may start
before that review, per Johan's instruction. Raw test/benchmark receipts stay outside Git.

| Finding | Required correction | State |
|---|---|---|
| 1 | Redis recovery registers one handler | Implemented; unit regression failed with 3 deliveries, now passes; native reconnect test added. |
| 2 | Incompatible distributed scope fails soft and preserves queues | Implemented; fail-soft/queue regressions pass; actionable scope health tests pass. |
| 3 | Set before verified scope queues replacement invalidation | Implemented; startup replacement/flush regression passes. |
| 4 | Retention revalidation, warning and health | Implemented; recovery revision race reproduced and fixed; active retention warning reset tested. |
| 5 | Expiry after serialization never trips breaker | Implemented; expiry and expired replacement regressions pass without breaker trips. |
| 6 | Unknown mandatory frame fields/operation regression | Implemented; valid MC1 frames with unknown mandatory field/operation added. |
| 7 | HTTP/admin invalidation reports unconfirmed work | Implemented; six method/HTTP admin mutations return 503 and retain recovery in endpoint tests. |
| 8 | Only queued HTTP invalidations block L2 | Implemented; unrelated slot stays available during a held advance. |
| 9 | Bounded long-client identity and startup tag validation | Implemented; bounded names, injective hash-tag domain and startup validation under test. |
| 10 | Avoid irrelevant client-tag scans | Implemented; only registered/captured client tags scan resident entries. |
| 11 | Bounded admin-id dispatch and unavailable status | Implemented; observation-id validation precedes dispatch; unconfirmed delete returns 503. |
| 12 | Reuse bounded observation metadata/metrics | Implemented; observed metadata reused; maintained counters replace whole-store metric scans. |
| 13 | Narrow Generated contract, AppendPart for all shapes, refreeze fixtures | Implemented; new pre-release Contract1Final source prepared; explicit Contract1Final freeze from 3e82c06e; seven binary/API facts and native osx-arm64 smoke pass. |
| 14 | Reject unsupported synchronous custom-manager invalidation at startup | Implemented; custom-manager sync binding regression failed before correction and now passes. |
| 15 | One generic/version key grammar | Implemented; generic/version and keyed-generic grammar regressions pass. |
| 16 | Container-scoped module validation/binding | Implemented; selected-module isolation fixture added; validation state belongs to each container. |
| 17 | Snapshot class, docs/version stamp and lazy custom-manager hashes | Implemented; immutable snapshot class, build-version stamp, internal-only generator docs and lazy custom hashes. |
| 18 | Preserve MC0004 meaning | Implemented; MC0004 remains duration; MC0022 covers non-generic Task/ValueTask. |
| 19 | Exact ticks, diagnostic de-duplication, consistent empty settings and tag docs | Implemented; exact ticks, one diagnostic, empty-setting validation and canonical tag docs. |
| 20 | TryGet L2 policy identity/tag metadata | Implemented; local policy/tag lookup regressions pass; Redis/SQL shared scenarios added. |
| 21 | Built-in policy invalidation under manager decoration | Implemented; decorated-manager policy invalidation regression passes. |
| 22 | Override task cleanup, one startup entrypoint and NUL framing | Implemented; faulted no-op invalidation task cleared, single startup API and logical-key/version framing tested. |
| 23 | Clear telemetry, repair classification, decorator/NoOp semantics | Implemented; clear classification, repair suppression, decorator ownership and NoOp subclasses covered. |
| 24 | Raw artifacts outside Git/history and ignore guard | Raw artifacts removed from reachable history in 300625ac; obsolete gitleaks exemptions also removed. |
| 25 | Current .NET10/SipHash spec and explicit acceptance step | Spec revision 4 records implemented .NET10, SipHash and explicit final gates. |
| 26 | Concise current validation, final performance/source checkpoints | Completion claims superseded; fresh performance/source checkpoints and final CI still pending. |
| 27 | Changelog placement/SipHash and net10 TestHost | Migration notes moved under Unreleased; SipHash recorded; TestHost uses .NET10. |
| 28 | Stale-if-error respects request freshness | Six exception/503 regressions failed before the fix and now pass. |
| 29 | Same full type name in different assemblies stays isolated | Assembly-scoped keys, generic arguments and policy ids; cross-assembly value and override regressions pass. |
| 30 | Named HTTP options validate bounds at startup | Seven invalid-bound regressions failed before the fix; valid zero/large bounds and deadline saturation also covered. |

CI follow-up: replace the two-second real-time assumption in the controlled
assertion-cleanup fixture with deterministic synchronization and an appropriate hang
guard, preserving the original exception and complete cleanup assertions.

Additional regression cases found during self-review: stale retention acknowledgements after a failure,
expired replacements retaining old L2 values, stale mismatch health after recovery, short-name aliases
of hashed long-client tags, and generic type arguments placed after keyed-registration identity.
Each defect is covered explicitly; the complete PR review and final receipts are still pending.

Self-review also reproduced an admin-id collision: GET reported a manual method entry while DELETE removed an HTTP observation with the same text. DELETE now follows GET’s method-first precedence; a regression checks both removals and that the untouched HTTP entry still hits.

## Independent review before CI

Reviewed the complete PR against main `8402ad2f70f4a6569ed081ba928ddfd4ebcb52f7`, including the
cumulative runtime changes, generated facade/emitters, provider/schema/wire operations, HTTP protocol/storage/admin,
policy publication and retirement, telemetry/privacy, package boundaries and the cost/timing/CI gates.
The reviewed source checkpoint is `3e82c06e4804b51dfa1e13e8a56798da5727a449`; subsequent fixture-only changes
replace the unpublished historical images in AOT and account explicitly for longer assembly-scoped miss keys.

Checked invocation binding and prefix/full-hash agreement; generic/keyed/version spelling; exact-key NUL framing;
policy identities and retirement gates; scope revalidation and replacement repairs; epoch/metadata deadlines;
owned recovery and disposal; Redis handler ownership; HTTP caller/variant isolation, stale directives and budgets;
admin confirmation/dispatch; telemetry ownership and safe labels; exact package dependencies and explicit ABI freezing.
The additional admin collision above failed before correction and now passes. The review found no remaining
source blocker. A generic version-specific Pattern requires the closed method name/type arguments; the unversioned
plain name matches all specializations. This is now explicit in the public helper documentation.

Local receipts (stored outside Git): Core 1,087 passed plus one opt-in soak skip, generator integration 544 passed,
HTTP 369 passed, docs 120 passed, compatibility 7 passed and Native AOT smoke passed. CI-script tests: 55 with two
intentional skips; benchmark-script tests: 60 passed. The first aggregate local run exposed the longer-key miss
budget and that test now passes with only the exact string-size increment charged. Native Redis/SQL, final
performance and full cross-platform CI remain final validation work, not claims established by this static review.
No CI or push occurred before this review.

Follow-up identity audit: identical runtime payload type names in different assemblies also collided in fallback keys, both as root arguments and nested object values. Two regression cases reproduced it. Fallback metadata now shares the same version-independent assembly/type identity as service/generic scope. Fresh timing waits for this correction; the prior source’s CI continues and its results remain separate.

The same audit reproduced identical keyed-enum names across assemblies; enum service-key scope now includes its assembly and unnamed values use invariant numeric text. Windows CI exposed the new Host startup tests trying to write the platform Event Log without permission. Those tests now clear default logging providers, preserving the validation failure assertions without machine-level logging dependencies. Linux and macOS CI passed the preceding checkpoint; its Windows failure remains a recorded failure.

Admin collision follow-up: an unobserved L2-only manual key can also look like an HTTP id. Its failing regression now falls through to ordinary confirmed method invalidation when no matching HTTP observation exists. The format check still prevents ordinary method keys from scanning HTTP metadata.
