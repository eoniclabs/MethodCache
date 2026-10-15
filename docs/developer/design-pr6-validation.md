# PR 6: implementation and verification checkpoint

Runtime source checkpoint: `9f84e393544cb4e55a197b106f2047f639c70122`, containing prefix candidate
`850cef8f3bc709eaf464b3c25e5ec898217fcc73`. Test-worker warmup correction: `c10f540`.
This is functional evidence, not a completed release or section-10 timing gate.

Implemented: protocol-neutral Core HTTP storage; separate 100 MiB local byte budget with conservative body/header/
metadata charging; native-only L2 with an internal HTTP codec; one bounded generation batch; global/client/static-tag/
4096 URL-slot epochs; captured publication namespaces; owned mutation recovery after cancellation; KeepCookies bypass;
escaped client tags; resident or bounded historical observation administration; Core HTTP telemetry and capability health.
Former public HTTP entry/storage/options APIs are removed. Method serializers and policy durations do not control HTTP.

The full net8/net9/net10 matrix passed **7,035 tests**, with no failures and three intentional soak skips,
over 46 project/framework runs. Native Redis/SQL were required; the linked HTTP contract facts ran against both.
They deliberately drop all backplane messages and cover NoOp HTTP storage, common Core-only tag/clear,
simulated future format namespaces and late origin/304 publication after POST/PUT/PATCH/DELETE.
HTTP unit tests: 290 per framework; real-transport integration: 16 per framework; docs: 119.
Changed executable source coverage against prefix 850cef8: **519/541 = 95.9%**.

Cultures (tr-TR/sv-SE), random CsCheck properties on net10/net8, enabled soak, demo, Native AOT smoke and all
172 native signature cases passed. Seven packed packages installed successfully; base smoke, actual .NET 8 SDK,
and add-on apps on net8/net10 passed, including independent-process hash-seed checks for both native providers.
Snippets and generated LLM documentation were regenerated before coverage. Frozen vendor fixtures were unchanged.

The first net8 run found 48 bytes of first-use state in one allocation worker; per-worker warmup fixes the probe
without relaxing its equality assertion (the extra warmup requests are counted explicitly). One later run failed
in Redis subscriber Ping with TaskCanceledException during new-container setup. This is retained as an unexplained
infrastructure observation; a subsequent complete matrix passed. It is not evidence of a production fix.

Red-before-green findings in the new implementation:

- Static tag invalidation initially missed the Core HTTP sink on one method-manager path.
- An oversized body was buffered beyond the entire HTTP budget before rejection; remaining budget now bounds the read.
- A method key beginning with `http:` was incorrectly treated as an unknown HTTP observation id.
- ConfigureHttpClientDefaults did not apply HTTP options to each final named client; warnings also shared the unnamed state.
- HTTP could select old metadata while common tag recovery was pending after an unrelated key acknowledgement closed the probe.
- HTTP-only health initially reported unsupported method statistics as zero-valued data; capability separation is covered.

Deterministic coverage uses a native-capability in-process model with FakeTimeProvider and explicit operation gates.
It is not evidence of network-provider atomicity. Tests cover metadata loss, reverse completion order, hanging reads/
advances, cancellation after origin, recovery without repeating origin, observation bounds/restart/expiry, query/body/
credential canaries, escaped clients, NoOp/reverse registration and independent method/HTTP budgets.

Remaining: the final audit's HTTP shutdown/policy-log fixes live in the descendant final-gates worktree and
need their full gates before the series is accepted. Section-10 per-change/cumulative timing and external
cross-platform CI remain pending. HTTP workloads validate that measured hits never call origin and that misses/304
do actual origin work. Functional green does not constitute a performance pass.

Additional boundary fixes: distinct isolated-surrogate caller headers initially aliased through UTF-8 replacement;
variant/header frames now preserve exact UTF-16. URI epoch framing stays frozen so representation format changes
cannot move invalidation to a different URL slot. A pinned URI vector failed before restoring that separation.
The maximum accepted client name now treats its implicit escaped tag separately from custom-tag length limits.
Real native Redis and SQL contract facts passed with no skipped infrastructure tests (2026-10-15).

## Publication and local slot follow-up (2026-10-15)

The initial Nimbus pair is diagnostic, not section-10 acceptance. It identified response publication for
further allocation work. The follow-up preserves frozen UTF-16LE SHA-256 identities, hashes without
intermediate input/digest arrays, avoids temporary body-budget target strings, and reuses immutable
metadata only when client, tags and format match. Replacements always recompute cost, timestamps and
selector state. Local reverse indexing now follows the bounded generation slots, releasing all bodies
whose namespace an invalidation retires. Native observations remain indexed by exact URL.

Identity and budget allocation probes and a colliding-slot cleanup fact failed before the fixes.
The updated HTTP unit suite passes all 320 cases on net10.0; 49 selected Core property/engine tests
and all three host startup facts pass. Windows CI previously exposed its default EventLog logger
masking the intended missing-Core startup exception with an access-denied logging exception. The
host registration tests now clear host logging providers; no machine permissions are changed.
Selected red/green receipts are in `plans/design-benchmarks/http-publication-followup/`.
Full descendant release gates and timing remain pending. Johan subsequently approved dropping .NET
8 and 9: a separate following PR changes the supported matrix to .NET 10 only; prior receipts remain
historical evidence for their original revisions.
