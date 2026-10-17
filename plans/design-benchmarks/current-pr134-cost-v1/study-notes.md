# Current PR 134 cost gate against its direct PR base

All 67 cases pass with **zero failing costs and zero accepted exceptions** against
419c786d949d361cd2acd418178e4b0a8e21a860. The measured head production sources match
published 5257fb63fb72393c817d74292e989299ede374d2 byte for byte (224 tracked files).

This comparison reuses complete deterministic cost rows from two preserved studies:
the baseline side of the original per-change PR 134 cost profile and the head side
of the current compact-telemetry cost profile. Both use the same seven compiled main
harness source files and the same pinned SDK/runtime and backend images. The extra
prefix source in the head preparation catalogue is not compiled by the main cost
project. Original timestamps, binary/source catalogues and receipts remain explicit.

These are **cost measurements**, not paired timing launches. The raw `ns` fields
remain unchanged diagnostic values; they establish no CPU acceptance. The current
cumulative baseline comparison and HTTP time failures remain separate.

Reproduce the cost verdict from this directory:

```sh
python3 compare.py base.jsonl head.jsonl --accepted accepted.json --markdown report.md
```

See `provenance.json` for source and input hashes. The original evidence is preserved
in `../per-change-cost-v1/` and `../http-compact-telemetry-cost-v1/`; this report does
not change their historical source points or verdicts.
