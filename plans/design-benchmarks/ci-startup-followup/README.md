# CI startup and generated-file follow-up

Three portable generated-filename cases fail before the fix. The original file write also fails for
ASCII/type names on macOS; UTF-8 filename bounds cover Linux Unicode names. The fixed generator keeps
existing short names and hashes only long hints. All 542 generator integration cases, 66 generator unit
cases and 112 benchmark cases pass; real benchmark compilation retains generated-source file emission.

Two controlled Redis FailFast first-ping cases fail before readiness retries. Six FakeTimeProvider cases
pass afterward, including hung-ping/deadline bounds and unrelated-exception propagation. All 33 Redis
cases then pass against required native Redis. The first full coverage run exposes another existing
policy-state sync assertion (101 versus 201). Its setup now awaits the existing native subscription gate,
without weakening any value/factory assertion. The second complete coverage run passes all sixteen
projects with required real Redis and SQL: see the manifest's derived counts and original log.

The coordinator regression fails before server-side timeout is added. Existing linux.sh cleanup already
passes its process-group termination test; the log historically named cleanup-red is a successful
pre-fix control, not a claimed failure. All 49 CI Python cases complete (47 pass, two SDK-10.0.300+
publishing checks intentionally skip on this local SDK). The actual Nimbus uutils timeout fixture
terminates its own fake job, calls only run-labelled cleanup and returns 125; no exact 124 code is assumed.
No actual CI/container/service was terminated by those fixture tests.

The old B925 SSH deadline at 3600 seconds left remote containers/testhosts behind. The new remote timeout
allows linux.sh cleanup, while the client watchdog leaves 60 seconds of transport grace. This affects
future installed passes; it does not clean up the existing orphan. PR136 supplies the independent mirror
GC fix; no duplicate fix was authored here. Original failed statuses are not replaced by local passes.

Logs preserve their original and normalized stored digests. No timing acceptance is claimed.

Native macOS AOT smoke passes, including frozen vendors; all 175 exported signature cases compile and execute their compilation check. Known trimming warnings remain. Initial local invocations used the Linux-only CI job, then a /tmp path affected by macOS symlink resolution; both failed invocations are retained separately. Corrected host-native checks use an in-worktree export.

The separate PR136 commit 21fc48e is integrated without changing its GC fix. Its real mirror-maintenance regression and all 50 CI Python cases complete (48 pass, the same two newer-SDK publishing checks skip). The clean-head docs job also passes.
