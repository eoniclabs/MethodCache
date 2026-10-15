# Local epoch allocation probe isolation

An older final-head Linux CI run (`cf4caa64f644`, run `20261015-162910-cf4caa64f644`)
failed `UnchangedLocalEpochVector_DoesNotAllocatePerLookup`: expected zero bytes,
observed 32,664. The original diagnostic is retained. This observation does not
establish the source of those bytes or a production allocation regression.

The probe now creates its store, performs the same 1,000 warmups and measures the
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
