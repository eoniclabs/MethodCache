# Local epoch allocation probe isolation

An older final-head Linux CI run (`cf4caa64f644`, run `20261015-162910-cf4caa64f644`)
failed `UnchangedLocalEpochVector_DoesNotAllocatePerLookup`: expected zero bytes,
observed 32,664. The original diagnostic is retained. This observation does not
establish the source of those bytes or a production allocation regression.

## Original dedicated-thread stage

That revision of the probe creates its store, performs the same 1,000 warmups and measures the
same 10,000 synchronous captures on a dedicated background thread with execution
context flow suppressed. The exact zero-byte assertion remains outside the
measurement. Exceptions propagate to the test; an incomplete local ValueTask
fails rather than changing the operation being measured. Shipped source is unchanged.

Sensitivity check: temporarily replacing the memoized identity return in
`HttpResponseStore.CaptureLocal` with `new string(previous.Identity.AsSpan())`
caused this revised test to fail with 1,520,000 bytes instead of zero. The
production file was restored byte for byte before the green runs.

All 320 HTTP tests then passed on each of net10.0, net8.0 and net9.0 in the
historical HTTP PR checkout (SDK 10.0.103, macOS arm64). These older targets only
validate that stacked checkout; the following .NET 10 PR removes their support.
Latest-head cross-platform CI remains separate evidence.

## Fixed steady-state batches and identity reuse

The later Linux/tr-TR run `20261015-181718-94ee6f0c1525` still failed the dedicated-thread
probe: expected zero, observed 680 bytes. The original output is retained in `fixed-batches/`.
The cause remains unknown; neither a runtime allocation nor a production regression is established.

The revised test completes exactly three fixed 10,000-capture batches after the original 1,000
warmups, and requires at least one batch to allocate exactly zero bytes. No byte tolerance or
adaptive retry is added. This checks sustained per-call allocations; it does not assert that
every invocation is free of arbitrary transient runtime allocations. Every one of the 30,000
captures also must synchronously return the same identity object as the final warmup. A single
returned identity copy therefore fails, even if other batches allocate zero.

Two temporary production mutations establish sensitivity. Copying the identity only at the first
measured capture fails the reference-reuse check. Adding an escaping temporary 128-byte buffer
to each capture, while returning the original identity, fails with **1,520,000 bytes in each of
the three batches**. The production file is restored byte for byte before testing the final fixture.

All 320 HTTP tests pass on net10.0 in the default, tr-TR and sv-SE cultures on macOS ARM64.
These are fixture checks, not a production fix, a complete latest-head CI run or section-10
performance acceptance. The no-allocation performance gate remains required separately.
