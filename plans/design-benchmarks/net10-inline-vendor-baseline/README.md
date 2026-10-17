# Frozen .NET 10 vendor coverage

The original contract-1 vendor was compiled for .NET 8. Running it on .NET 10
preserves its materialized-key path; it does not exercise the inline constructor
or reference-based lookup ABI required by G14/F05. The new metadata coverage fact
fails before the fix because no frozen image references `KeyBuilder..ctor`.

A separate `Contract1Net10` checkpoint is frozen from committed source
`ba86985c9c7a1e248a5a0a0ee07b0dc570f7b3d2`, real package
`1.0.0-gba86985c9c`. The original manifest, images and all source files remain
byte-for-byte unchanged. Ordinary tests/AOT reference both checkpoints as binaries.
The explicit freezer now refuses to overwrite an existing manifest and supports
the separate .NET 10 directory. Both immutability/path guards pass.

All nine compatibility facts pass, including actual net10 sync/Task/ValueTask
hits, invalidation, record/fallback and generic keys, third-party CacheInterface,
keyed/scoped services and the unknown-contract startup failure. Metadata coverage
checks the constructor and both inline/materialized facade members.

Sensitivity proof removes the constructor only in a temporary local runtime
mutation. PublicApiAnalyzers first reject it with RS0017. Suppressing just RS0017
for that mutation invocation allows the independently frozen vendor to execute:
it fails with `MissingMethodException`. The runtime source is restored in finally;
all image hashes remain unchanged. All nine facts pass again with normal analyzer
settings after restoration. No analyzer suppression is added to the repository.

The Native AOT smoke app additionally invokes the new frozen vendor's inline
sync/Task/ValueTask paths, tag invalidation, third-party interface and closed generic
service. Native `osx-arm64` publication and execution pass. No shipped library source
changes, and the already frozen performance workloads remain valid.

`manifest.json` retains original/stored hashes for ten logs and hashes for every
unchanged historical baseline file. Only local absolute paths are normalized.
This is local evidence; current published-head CI and timing remain separate gates.
