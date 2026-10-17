| Case | Base ns | Head ns | Head/base | 95 % interval | Verdict |
|---|---:|---:|---:|---|---|
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=False) | 58.0 | 29.7 | 0.482 | 0.468-0.496 | faster |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=True) | 59.4 | 28.8 | 0.474 | 0.447-0.503 | faster |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=False) | 63.6 | 31.1 | 0.475 | 0.458-0.492 | faster |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=True) | 63.0 | 30.2 | 0.474 | 0.456-0.493 | faster |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=True) | 68.5 | 30.2 | 0.446 | 0.433-0.458 | faster |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=False) | 9275.5 | 7328.7 | 0.791 | 0.777-0.804 | faster |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=True) | 9128.9 | 6958.4 | 0.771 | 0.759-0.783 | faster |
| GeneratedContractBenchmarks.FallbackHit() | 223.7 | 211.1 | 0.945 | 0.933-0.958 | faster |
| GeneratedContractBenchmarks.SyncHit() | 42.5 | 41.9 | 0.985 | 0.963-1.007 | same |
