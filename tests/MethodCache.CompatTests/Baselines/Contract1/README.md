# Generated contract 1 baseline

The checked-in DLLs are inputs to compatibility tests. The solution and AOT smoke app reference the vendor
images as binaries; they never build the source projects below `source/`. `FrozenVendor.Contract99.dll` is loaded
explicitly by its negative bootstrap test, so ordinary app startup does not register the incompatible module.

`manifest.json` records the full source commit, real package version, Core/generator hashes, source hashes and
image hashes. The vendor covers sync, Task, ValueTask, parameterized tags, invalidation, generic methods,
closed generic interfaces, Unicode/fallback keys and a separately compiled third-party CacheInterface.

To intentionally create the initial baseline, first commit and verify the implementation and fixture sources,
then run from the repository root:

```bash
python3 build/ci/freeze_generated_contract.py --source-commit <full-verified-HEAD-SHA>
dotnet test tests/MethodCache.CompatTests -c Release
```

Review and commit the resulting images and manifest separately. Later Core/generator changes must run against
these images unchanged. Do not regenerate a baseline to make a compatibility failure pass. A new contract
gets its own baseline directory and keeps this one.
