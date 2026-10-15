# PR 6: implementation and verification checkpoint

Worktree: `codex/http-core-store-v1`, currently based on the initial prefix candidate `06542df`.
The final prefix implementation must be incorporated before publishing this stack member. This is an
in-progress checkpoint, not a completed release or timing gate.

Implemented: protocol-neutral Core HTTP storage; separate 100 MiB local byte budget with conservative body/header/
metadata charging; native-only L2 with an internal HTTP codec; one bounded generation batch; global/client/static-tag/
4096 URL-slot epochs; captured publication namespaces; owned mutation recovery after cancellation; KeepCookies bypass;
escaped client tags; resident or bounded historical observation administration; Core HTTP telemetry and capability health.
Former public HTTP entry/storage/options APIs are removed. Method serializers and policy durations do not control HTTP.

Latest completed HTTP unit run: **289 passed** on net10.0. The intermediate HTTP registration/real-transport suite passed
**16 tests**, and docs passed **119 tests**. Later source changes still require the complete matrix. Redis and SQL linked
native contract tests compile, but have not yet run against the real backends. They deliberately drop all backplane
messages and cover NoOp HTTP storage, common Core-only tag/clear, simulated future format namespaces and late origin/304
publication after POST/PUT/PATCH/DELETE.

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

Pending: remaining lifecycle/security review and acceptance boundaries; full net8/net9/net10 and real Redis/SQL matrix;
>=80% changed-source coverage; cultures/random properties/soak; frozen vendors and Native AOT/corpus; installed packages
and actual SDK 8; snippet/LLM regeneration and documentation checks; section-10 per-change and cumulative timings.
HTTP workloads in `httppath` validate that measured hits never call origin and that misses/304 do actual origin work.

Additional boundary fixes: distinct isolated-surrogate caller headers initially aliased through UTF-8 replacement;
variant/header frames now preserve exact UTF-16. URI epoch framing stays frozen so representation format changes
cannot move invalidation to a different URL slot. A pinned URI vector failed before restoring that separation.
The maximum accepted client name now treats its implicit escaped tag separately from custom-tag length limits.
Real native Redis and SQL contract facts passed with no skipped infrastructure tests (2026-10-15).
