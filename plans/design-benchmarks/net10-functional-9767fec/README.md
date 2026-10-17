# Current standalone .NET 10 functional checkpoint

Exact head `9767fec6adcfe41cbe83862915ba922e06770030`, CI run `20261015-200756-9767fec6adcf`.
All ten Linux jobs and macOS/Windows build/unit jobs pass; all three platform and
aggregate statuses were verified success on 2026-10-15 at 18:26 UTC.

The main Linux job passes 2,519 tests in 16 projects, zero failures and one intentional
opt-in soak skip. Required Redis/SQL integration, 90 randomized properties, enabled
soak, both cultures, demo, 172 AOT signatures, installed packages and docs pass. This
includes the current strict body-budget, telemetry and epoch allocation fixtures.
Exact per-suite counts, per-job exits, status timestamps and original/stored log hashes
are retained in the manifest. Repository copies normalize CRLF to LF.

Shipped sources exactly match runtime 12bf99d877c33925fe0d0037f45e652d0260d8a4.
This is current standalone functional acceptance, excluding PR132's constructor,
SipHash/hash-choice approval, section-10 timing, lower-stack head statuses or any
merge authorization. Subsequent receipt-only edits leave these test/runtime sources
unchanged. The older e84a549 receipt predates the strict body-budget fixture.
