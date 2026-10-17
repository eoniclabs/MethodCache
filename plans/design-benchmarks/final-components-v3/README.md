# Final-runtime component and memory-cache diagnostics

All 30 predeclared cases execute in separate fresh processes with four warmups
and twelve 150ms actual iterations: eight short/long prefix and buffer-growth
key-build cases, eighteen int/GUID whole-span/prehashed/ready-string lookups, and
four IMemoryCache ready/built-key and sync/Task references. [Complete table](components.md).
All 54 component cost cases and the actual lookup/growth/listener/origin guards
also pass without execution errors. Runtime/SDK/CPU, workstation GC, actual CPU
affinity and interference observations are retained.

This is one current-runtime diagnostic, not an independent paired before/after
comparison or a time-gate pass. Different key lengths, ready keys, constructed
keys and Task creation are distinct operations. The table does not establish a
portable product speedup or the proposed 15–20ns gain. Full-path paired gates
remain separate and currently open. Earlier missing-package and guard-namespace
compile failures produced no timing rows and remain preserved on the build host.
