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
