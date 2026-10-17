# Native policy invalidation fault acceptance

P11/P12 acceptance uses one unchanged shared contract in Core/model, Redis and SQL Server tests.
Three scenarios per backend pass (9 facts, no infrastructure skips), on .NET 10.0.3/macOS ARM64.
Redis and SQL Server were fresh Testcontainers instances with METHODCACHE_REQUIRE_INFRA_TESTS=true;
each scenario has its own metadata prefix, and SQL uses an owned schema dropped in finally.

- Partial stamp/publication failures: Requested=3, Local=3, Distributed=2, Queued=1, Published=1,
  PublicationQueued=1; both queues drain independently, and all three metadata stamps are read back.
- A stamp held after local mutation: caller cancellation ends its wait, owned recovery completes the
  native write, shutdown drains it and the confirmed bytes are read from that provider.
- Invalid/empty/duplicate batches and cancellation before mutation: the original L1 entry is checked
  directly before a public lookup can repopulate it; no stamp was written; valid distinct targets confirm.

Faults wrap the supplied real provider, never replace its successful operations. A mutation that
acknowledges the stamp without calling the provider fails all three real Redis scenarios at the
native read-back assertion. The wrapper was restored, then all three backend contracts passed.
The mutation receipt predates adding a unique per-scenario prefix and the explicit L1-preservation
assertion; the final green receipts include both. No production source or benchmark workload changed.
