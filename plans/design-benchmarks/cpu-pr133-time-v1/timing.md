| Case | Base ns | Head ns | Head/base | 95 % interval | Verdict |
|---|---:|---:|---:|---|---|
| GeneratedContractBenchmarks.SyncHit() | 63.8 | 62.7 | 0.983 | 0.954-1.012 | same |
| GeneratedContractBenchmarks.TaskHit() | 70.3 | 67.0 | 0.954 | 0.910-1.000 | **too noisy** |
| GeneratedContractBenchmarks.ValueTaskHit() | 76.9 | 74.6 | 0.974 | 0.942-1.007 | **too noisy** |
| GeneratedPrefixHitPathBenchmarks.SyncHit(LongPrefix=False,Listening=False,ArgumentKind=Int) | 49.5 | 56.3 | 1.109 | 1.068-1.152 | **too noisy** |
| GeneratedPrefixHitPathBenchmarks.SyncHit(LongPrefix=True,Listening=False,ArgumentKind=Int) | 117.5 | 120.2 | 1.026 | 1.005-1.048 | same |
