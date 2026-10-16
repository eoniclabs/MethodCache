# Direct default HTTP variant selection

The cold-miss profile found a set and sorting pipeline on every publication,
even without configured key headers or Vary fields. Four allocation regression
cases first fail at 536 B per private selection and 152 B per shared selection
(`red.log`). The direct default branch returns a fresh private two-element array
or the shared empty array. It retains Authorization/Cookie isolation; configured
or nonempty Vary headers continue through the original combination path.

All four cases then pass, including mutation of a returned private array and
changes to options after an earlier selection. Budgets are 48 B/private and
8 B/shared, including an 8 B bookkeeping allowance. All 326 HTTP tests and 115
benchmark tests pass, and both changed executable lines are covered. Existing
caller, Vary, HTTP security and bounded cold-batch tests remain active.

A diagnostic over 64 independently reset batches of 8192 actual cold HTTP misses
reports about 8336.16 B/op before and 7842.17 B/op after. These are local allocation
diagnostics, including amortized first-slot costs. The probe source is retained;
copy it under artifacts/http-alloc-probe to reproduce. Runtime event samples are
supporting diagnostics; their counts are not exact object counts. The reported
elapsed durations include resets and tracing and are not accepted timing data.

The full cumulative HTTP time regression is still open. This small correction
cannot substitute for three independent alternating timing pairs or final CI.
Original/stored file hashes are retained in manifest.json; no binary trace is
included in the repository.
