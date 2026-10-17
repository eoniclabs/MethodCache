# Reuse bounded SQL epoch command text

Every shared HTTP generation capture rebuilt the entire SQL batch text. A
three-row shape allocates 21200 B in the extracted builder. The regression fails
before caching (`red.log`); its exact uncached extracted source is preserved in
`red-source.cs.txt`. This red setup is distinct from the untouched parent: it
isolates the existing text construction without changing its database rules.

Retain one immutable last-shape command per provider. The shape uses sorted row
positions rather than caller positions, so changing key order can reuse the same
text. The reader maps each row back through the sorted key/index array. Changing
the count replaces the one retained template; provider tables remain independent.
Concurrent shape changes return their own complete immutable template. No key,
epoch value, SqlCommand, parameter or connection is cached.

The warmed text regression permits at most 8 B/call of incidental runtime
bookkeeping, instead of rebuilding the 21 kB three-row batch. Unit tests cover
counts 3/128, invalid counts, shape replacement, independent provider tables and
concurrent counts. The real-SQL case advances a key, reorders the vector, then
checks duplicates against the exact expected epochs. Existing permanent-read,
retention-repair, cross-instance and rollover tests remain active.

All 45 SQL unit tests, five epoch cases and 67 SQL integration facts pass with no
infrastructure skips. Both unit and full integration coverage execute all 28
changed executable lines. This is correctness and allocation evidence; no timing
speedup or complete design acceptance is inferred. The separately frozen six-run
SQL UPDATE correction study excludes this later optimization.

Original/stored hashes and exact source hashes are retained in manifest.json.
Logs normalize only paths, line endings and trailing whitespace.
