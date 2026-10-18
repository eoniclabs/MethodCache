# Frozen contract 1: .NET 10 inline keys

This additional baseline exercises the `KeyBuilder(MethodCall, Span<char>)`
constructor and reference-based inline lookups emitted for .NET 10. The original
`Contract1` images were compiled for .NET 8 and exercise materialized keys. They
remain byte-for-byte unchanged; running those images on a newer runtime does not
change which generator branch was compiled into them.

Source commit: `ba86985c9c7a1e248a5a0a0ee07b0dc570f7b3d2`.
Packed package: `1.0.0-gba86985c9c`. `manifest.json` records the target, full source
commit, actual Core/generator hashes, six source-file hashes and three image hashes.
Source files were committed and built before the explicit freeze.

Ordinary compatibility and AOT builds reference the images as binaries. They never
build these source projects. Compatibility tests check both the metadata member
references and actual cached calls: sync/Task/ValueTask, invalidation, fallback,
generics, scoped/keyed services and third-party CacheInterface. The deliberate
contract-99 module is loaded only by its negative startup test.

To reproduce the explicit initial freeze, use a separate worktree at the recorded
source commit, before the manifest/images were added, and run:

```sh
python3 build/ci/freeze_generated_contract.py \
  --source-commit ba86985c9c7a1e248a5a0a0ee07b0dc570f7b3d2 \
  --baseline-directory Contract1Net10 --target-framework net10.0
```

The freezer refuses an existing manifest. Future checkpoints use a new directory;
never replace either baseline to make a test pass.
