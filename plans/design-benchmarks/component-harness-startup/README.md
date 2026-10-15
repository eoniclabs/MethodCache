# Component harness startup repair

The preparation-only Nimbus guard failed before timing. Its cleanup exception masked the original
provider-construction failure. A fresh local harness reproduced `CallSiteFactory.Populate` throwing
`NullReferenceException` during `BuildServiceProvider`, with setup outside the cleanup block.

The external harness resolved `Microsoft.Extensions.DependencyInjection/6.0.0` transitively through
BenchmarkDotNet alongside `Microsoft.Extensions.DependencyInjection.Abstractions/9.0.9`. Explicitly
referencing DI 9.0.9, as the repository's benchmark test project already does, repairs this harness.
This changes neither the measured workload sources nor the shipped library dependencies.

The generator now includes `--verify-components`. After the repair, freshly generated local Release
harnesses pass all three lookup lengths; eight prefix/buffer/listener combinations with 100 repeated
sync/Task/ValueTask hits and one origin load; actual long-key buffer growth; and four memory-cache
reference paths. Both PR5 runtime `2f4a91d0c9144dd0cd9de3482291f365c4443508` and prefix runtime
`9c31e3b150b269d10dec99e2e93043918a718579` pass on macOS ARM64/net10. The prefix runtime's shipped
source matches frozen timing source `850cef8f3bc709eaf464b3c25e5ec898217fcc73`.

`manifests.json` preserves exact workload checksums/runtime SHAs and the resolved before/after DI
package versions. The original Nimbus and local failures remain separate from green receipts.
These are executable setup checks, not performance results. Native-host preparation and the
complete accepted timing matrix remain outstanding.
