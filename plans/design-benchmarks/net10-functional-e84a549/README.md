# Full .NET 10 functional checkpoint e84a549

Exact head: e84a54984f7a9876e236b281bc4b0e1236a4a9a9, CI run
20261015-190437-e84a54984f7a. All Linux jobs and macOS/Windows build/unit jobs pass.
All three platform and aggregate GitHub statuses were verified success on 2026-10-15
at 17:17 UTC. The manifest retains status timestamps, per-job exits, individual Linux
suite counts and SHA-256 digests of the selected logs. CRLF is normalized to LF for Git storage; original-source
checksums are retained separately.

The main Linux test job passes **2,519 tests in 16 projects**, zero failures and one
intentional opt-in soak skip. Required Redis/SQL, 90 randomized properties, enabled
soak, tr-TR/sv-SE cultures, demo, diff coverage, 172 Native AOT signatures, installed
packages and generated docs pass. Windows/macOS build and unit jobs also pass.
This includes the final epoch and telemetry allocation fixtures and distinct null-marker
fact. Their sensitivity mutations and original failures remain in separate receipts.

Shipped sources exactly match runtime 12bf99d877c33925fe0d0037f45e652d0260d8a4.
This proves the standalone stack at the stated SHA; it does not include unfinished
PR132/SipHash, prove lower-stack head statuses, or establish any performance result.
Subsequent receipt/report-only commits do not change those shipped or test sources.
