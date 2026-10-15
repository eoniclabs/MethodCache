# HTTP identity allocation probe isolation

The exact identity-byte assertion intermittently failed in full CI on both .NET 9/Linux and .NET 10/macOS.
The original failures are recorded beside this file. The assertion's expected value and 100-call measurement
are unchanged. The synchronous probe now runs on its own thread with ExecutionContext flow suppressed;
thread/delegate creation, warmup, completion and assertions remain outside the measured interval.
This isolates the probe from the test runner's worker/ambient context. It does not establish the source
of the historical extra allocations or claim a production fix. Latest-head CI must verify the fixture.

To check sensitivity, a temporary production mutation allocated an escaping UTF-16-sized byte buffer
before Identity. All four input cases failed the exact byte comparison (including empty/malformed/long
keys). The original production file was restored byte-for-byte, then all 320 HTTP tests passed on .NET 10.
No shipped source or benchmark workload changed; final runtime performance candidate remains 28ab9b2.


A subsequent Linux culture run on final head `771980a` reported **16,024 bytes in
our output-only reference and 16,000 in Identity**. The owned thread alone was
therefore insufficient to make the one-batch reference deterministic. This is
retained in `owned-thread-reference-ci-failure.log`; the source of the additional
24 reference bytes is unproven. Each path now takes exactly three fixed batches
of 100 calls and compares their minima, still with exact equality and no byte
tolerance. This checks sustained per-call temporary copies; it is not a test that
every individual invocation is free of any transient runtime allocation.

The revised three-batch probe rejects a temporary production mutation that copies
the UTF-16 bytes to a new input-sized buffer before hashing: all four keys fail,
while digest semantics are unchanged. After byte-for-byte source restoration,
all 320 HTTP tests passed on each historical net8/net9/net10 target and under
tr-TR/sv-SE on net10. These are fixture sensitivity checks, not timing acceptance.
