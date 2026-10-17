# Diagnostic GUID hash/lookup isolation

The independently fixed sync-only confirmation passes precision: runtime adaptive hash/lookup is 9.3% faster, ratio 0.907 with 95% interval 0.884–0.930. This isolates the hash/lookup mechanism while preserving generated binding, retirement checks and telemetry. It does not isolate SipHash rounds alone or approve a production rollback.

All 40 predeclared launches / 20 alternating pairs are independently verified:
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
file checksum. Archive SHA-256: `23d784e19298067a44792dc33939b52ba83bb6b5ade5479cdaf67ad5d3872d49`.
