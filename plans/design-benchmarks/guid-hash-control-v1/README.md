# Diagnostic GUID hash/lookup isolation

The complete three-return-shape report fails precision on all three cases. Sync estimates a 10.1% gain, but no timing acceptance or cause is inferred from this noisy original alone.

All 60 predeclared launches / 10 alternating pairs are independently verified:
same compiled workloads, runtime/GC, actual affinity, interference guards and
222 build-file checksums. The baseline includes the verified node-hash reuse fix;
head adds one constructor argument selecting the existing internal runtime string
comparer hook. This is **diagnostic only**, not a production candidate. The
current generated binding, live generation checks, telemetry and key text are
identical. Production retains its process-secret SipHash prefix comparer.

Original f43854d explicitly constructs ConcurrentDictionary with its default
non-randomized string fast path. .NET10's constructor uses an adaptive comparer
for default/ordinal strings and switches after collisions; see the
[primary runtime source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Collections.Concurrent/src/System/Collections/Concurrent/ConcurrentDictionary.cs).
The custom prefix comparer cannot use that transparent hash switch, which is why
it uses a process-secret hash. All original complete results stay separate; no
individual launch is selected and no acceptance rule changes in this diagnostic.

Only prepared.json's binary dictionary uses reversible path/hash records. Run
restore-original-metadata.py with a new directory to verify every original archive
file checksum. Archive SHA-256: `1bdbbdb7020ccfb0cdc4b73f96b65e541e476fb900193f77ec8f942d988bd0f0`.
