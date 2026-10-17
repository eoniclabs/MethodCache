# Paired cost-profile timing gate validation

On merge head `5d9c829`, the new timing comparator returns success for imprecise speedups and accepted slowdowns, although §10 requires every interval wider than 6% to fail. It also silently drops an unmatched launch and overwrites duplicate cases. The seven regression tests run against the unmodified comparator first: four fail for those reasons (`red.log`).

The fix checks precision before any speedup/acceptance decision, requires paired launch files, rejects duplicate cases and validates finite positive times. The full benchmark Python suite then passes all 60 tests (`green.log`). Runtime, workloads, measured data and acceptance limits are unchanged. This validates the reviewer, not performance.

`manifest.json` retains original and stored log hashes, normalization and source hashes. Run the tests with `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s benchmarks/MethodCache.Benchmarks/validation -v`.
