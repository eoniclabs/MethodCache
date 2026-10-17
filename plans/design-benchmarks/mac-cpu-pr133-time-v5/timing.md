| Case | Base ns | Head ns | Head/base | 95 % interval | Verdict |
|---|---:|---:|---:|---|---|
| GeneratedContractBenchmarks.SyncHit() | 51.1 | 41.3 | 0.814 | 0.800-0.829 | faster |
| GeneratedContractBenchmarks.TaskHit() | 50.8 | 43.2 | 0.843 | 0.829-0.857 | faster |
| GeneratedContractBenchmarks.ValueTaskHit() | 54.7 | 45.8 | 0.851 | 0.838-0.864 | faster |
| GeneratedPrefixHitPathBenchmarks.SyncHit(LongPrefix=False,Listening=False,ArgumentKind=Int) | 34.8 | 36.8 | 1.063 | 1.033-1.094 | **too noisy** |
| GeneratedPrefixHitPathBenchmarks.SyncHit(LongPrefix=True,Listening=False,ArgumentKind=Int) | 86.7 | 89.8 | 1.039 | 1.021-1.057 | **slower** |
