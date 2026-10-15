# Controlled synchronization-context regression fixture

HTTP head f793d0a fails its macOS net10 Core suite in
BlockingOnAMiss_NeverNeedsTheCallersSynchronizationContext: the caller thread
did not join within ten seconds. The original reason remains unproven. Linux
and Windows passed; this failure is not attributed to removed runtimes.

The old fixture blocked the xUnit thread in Thread.Join and used real 10 ms
transport delays. The revised async fixture retains a dedicated blocking caller
with its recording SynchronizationContext. An explicit signal proves the outer
API returned an incomplete task before transport is released. The async test
controls each queued I/O completion, verifies actual reads and writes, and still
requires the exact result and zero context posts. It uses no sleeps or transport
timers; the unchanged ten-second deadline is solely a hang guard. The node and
controlled transport are disposed, and exceptions return through the caller task.

A temporary production mutation changes the load-wait continuation to capture
the caller context. The revised test fails with zero expected posts and one
actual post, then production source is restored byte for byte. Complete Core
suites pass 1,036 facts with one intentional opt-in soak skip under default,
tr-TR and sv-SE on net10. This is local fixture verification, not latest-head CI
or a production fix for the unproven original timeout.

The exact fixture is forwarded to the lower HTTP and .NET-10-only steps. Each
complete local net10 Core suite passes 1,023 facts, with one intentional soak
skip. Shipped source is unchanged; fresh latest-head CI remains required.
