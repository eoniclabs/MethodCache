# Isolate native L2 deadline assertions from late subscription callbacks

The actual d3126b71 coverage run fails the shared Redis contract's slow-factory
assertion: expected10,actual20. A subscription recovery is allowed to retire a
running load and prevent publication, so the deadline-only phase must not depend
on when native callbacks happen. This phase now detaches both timed nodes before
advancing the fake clock; native mutation/delivery assertions earlier in the
shared contract remain active and unchanged.

Two deterministic fault tests expose gaps in the test barrier: an already queued
callback still enters after unsubscribe (1expected,2actual), and unsubscribe
returns while an asynchronous recovery callback is unfinished. Both fail before
the barrier correction. Delivery handoff is now synchronized, future callbacks
are disabled at unsubscribe and all already-started tasks are drained. All three
barrier facts pass. This is test-support isolation, not a production guarantee
added to either transport.

The pinned native check retains ten fresh original Redis processes (all pass,
so the CI issue is intermittent), ten fresh candidate Redis processes and three
fresh candidate SQL Server processes. The coverage collector is enabled as in
the failing CI phase. Every TRX independently proves one executed/pass test and
zero skips. Candidate native cases and all1047Core facts pass, with the one
intentional opt-in soak skip. The Core DLL hash is identical across all builds.
All deadline values, fake-clock advances and expiry/fail-safe assertions remain
unchanged. No timing tolerance, retry or library behavior is weakened. New-head
three-platform CI remains required; this evidence does not prove the precise
scheduling of the original CI event.

All59originalarchivefilehashes verify. Every23fresh-processlog/TRX and controller,
patch, image/build receipts and original failing CI report are retained. Coverage
XML is not republished; the collector supplies the observed environment, not a
new coverage-percentage claim. Only newly created exact-label Redis/SQL containers
are used and removed inside the bounded shared-capacity lease. Shared providers
remain untouched. Archive SHA-256:
`90fe1db5a7813757cddc6c94b1242c252e9bd00d577872b76db4f7cd01588c83`.
