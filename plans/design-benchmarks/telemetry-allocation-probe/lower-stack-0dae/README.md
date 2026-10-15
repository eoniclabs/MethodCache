# Forward final fixtures to the lower stack

HTTP head 0dae335a3e6f702af15f6b26998c8497b6065734 failed its macOS unit job
in NoListeners_RequestAndOperationChecksAllocateNothing: zero expected, 1,880
bytes observed. The suite summary identifies the sole failed Core suite as
net9.0; net8.0 and net10.0 Core pass all 1,022 facts present in that branch.
All Linux jobs and Windows build/unit jobs pass. The original byte cause is
unproven. .NET 10 remains the final supported target; no old SDK is installed
or added back.

That branch still contained the old measurement with Assert.Null inside the
measured loop. Forward the already-verified final owned-thread, warmed, synchronous
NoInlining fixture unchanged. The measured single batch remains exactly 10,000
request/operation pairs and must allocate exactly zero bytes. No tolerance or
resampling is added. The earlier independent Request and BeginLoad sensitivity
failures remain in the parent receipt.

Also forward its existing distinct AppendNullPart fact: the previous nullable
string and null-part theory rows had the same computed xUnit ID, so one row was
not executed. Both comparisons remain covered with distinct tests.

Complete final local net10.0 Core suites pass 1,023 facts in the HTTP and .NET-10-only
checkouts, with one intentional opt-in soak skip and no duplicate-ID warning.
The two changed files exactly match the already-tested combined final stack.
Shipped source is unchanged. Fresh current-head three-platform CI and section-10
performance verification are still required.
