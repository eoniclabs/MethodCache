# Source review of frozen performance families

The frozen V4 analysis helper incorrectly labels `RoundTrip=True` as new work at PR4.
Review before the full timing dispatcher starts shows that all five earlier runtime points
already validate writes unconditionally. PR4 introduces an opt-out. The original archived
helper and its original test are preserved; their classification is superseded by
`benchmarks/MethodCache.Benchmarks/validation/summarize-design.py`.

Existing `RoundTrip=True` writes and all measured L2 reads stay comparable across PR4.
Only disabled-validation writes cross a behavior difference: older labels still execute
validation, whereas the new option removes it. Those opt-out rows are reported separately.
Neither this annotation nor a statistics interval grants automatic acceptance. KeepCookies,
HTTP generations and other behavior changes still need their documented manual review.

The corrected helper uses the unchanged pair-statistics source and complete 180-launch/
eleven-edge guards. New tests fail before the correction with six assertion subtest failures;
all twelve summary tests and all 52 benchmark-validation Python tests pass afterward.
`manifest.json` records exact old/new analysis hashes and source file hashes/excerpts at every
frozen runtime point. No benchmark means were inspected.

No deployed V4 file, protocol checksum, workload, runtime, process, launch order or sample
count is changed. The separate reviewer reads completed immutable results later. These
red/green checks prove the classification correction, not actual measurements or speed.

`instrumentation.json` records default-enabled statistics at all fourteen points and historical
null-provider registrations. `runtime-equivalence.json` and its diff show the only product
source change after B925 is the generator hint filename; runtime/build settings are unchanged.
These source reviews do not replace compiled inventory checks, functional CI or timing gates.
