# Native policy fault fixture: failed assertion must release its gate

Linux CI `20261015-231740-b92553dac51d` stalled in the Redis integration suite during diff coverage.
Read-only process inspection identified the exact Redis testhost. A private diagnostic snapshot was
captured without terminating the target. Its heap shows the controlled fault store's Started task
completed, Release task incomplete, held stamp write awaiting that Release, and owned shutdown awaiting
the write. The fixture's RunAsync state machine holds `Xunit.Sdk.EmptyException` with the safe message
`Assert.Empty() Failure: Collection was not empty` while unwinding through asynchronous disposal.

The fixture counted every historical receipt when checking its held write, and released its gate only
after all assertions succeeded. A failed assertion therefore hid its own failure behind an endless
cleanup wait. The snapshot establishes that failure path; the precise earlier producer of the already
confirmed receipt is not inferred. A deterministic regression explicitly supplies an earlier valid stamp.

The corrected fixture counts successful held writes separately, preserves the zero-before-release and
one-after-recovery checks, and still reads every recorded stamp back from the actual supplied backend.
A finally block releases the test gate before owned disposal, even after assertion failure. No production
shutdown, timeout, generation or provider behavior changes.

Two new regressions fail before the fix: a controlled assertion while held must preserve its exception,
and a prior confirmed stamp must not invalidate the held-operation check. Their original failures time
out on the blocked cleanup. After the fix, all five model contract cases pass, the complete Core suite
passes 1038 facts with one intentional opt-in soak skip, and the three original shared cases pass against
each real Redis/SQL provider with required infrastructure. These results do not turn the original stuck
CI run into a pass; latest-head CI remains required.

Only sanitized type/field-state diagnostics, the safe assertion message, original CI output and test logs
are retained. The private heap snapshot is not published. ClrMD 3 could not identify the dump version;
the exact .NET 10.0.12 DAC from that same target container was supplied. Managed stack names were
unavailable, so the diagnosis relies on observed async state/awaiter links rather than invented stacks.
