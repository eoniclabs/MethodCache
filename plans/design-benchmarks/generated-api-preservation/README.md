# Preserve every frozen Generated declaration

The constructor optimization in #132 removed `MethodCall.StartKey`. It belongs to
the original contract-1 API at `9294adb3bd562b502d18a428d452b0bc40199515`, although
the original net8 vendor images never called that inline entry point. A new guard
captures all 103 Generated declarations from that exact source. It fails before
the fix, identifying StartKey as the sole missing declaration.

Restore StartKey as a supported contract-1 entry point using the snapshot-bound
constructor. New generated code continues to use the constructor directly. The
guard checks both current Shipped and Unshipped API files and also rejects new
abstract members on GeneratedModule. PublicApiAnalyzers tie those declarations to
the actual public members. Neither frozen vendor checkpoint is regenerated.

All ten compatibility facts pass. All 1,043 Core facts pass, with the single
intentional opt-in soak skip. Four new factory cases cover empty/small/growing
buffers, escaping, Unicode, an intervening policy change and foreign bindings.
Changed executable source coverage against 9758f30 is 1/1 (100%). Native osx-arm64
publication/execution of the expanded smoke app pass again.

The runtime change adds only the legacy factory entry point. Existing fields,
lookup/loader methods, engine code, new generated calls and frozen benchmark
programs remain unchanged. The component factory selects the constructor when
available, even when StartKey is also present. No live timing input or worker is
modified, and no result is discarded because of this compatibility correction.
The new entry restores previously supported work; current constructor-path timing
does not measure the returned-builder copy cost of legacy callers.

`manifest.json` retains original/stored hashes for the seven red/green/coverage/AOT
logs. The frozen API snapshot records the full original source-file hash. The
preceding 9758f30 passes all fourteen external CI jobs; that checkpoint does not
establish the restored factory's final-head CI, which remains required.

Secret scanning initially flags the original Git commit identifier in the API
provenance field. Its commit-object hash and referenced source-file hash are
independently verified. One exact file/rule/line fingerprint excludes that finding.
