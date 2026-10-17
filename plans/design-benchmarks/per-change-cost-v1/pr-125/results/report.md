| Case | Bytes base | Bytes head | Redis base | Redis head | SQL base | SQL head | Gate |
|---|---:|---:|---|---|---:|---:|---|
| GeneratedContractBenchmarks.SyncHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.GenericHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.TaskHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.ValueTaskHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.StringHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.RecordHit() | 296.2 | 296.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.CollectionHit() | 896.2 | 896.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.FallbackHit() | 408.2 | 408.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.SyncMiss() | 984 | 984 | - | - | - | - | ok |
| GeneratedContractBenchmarks.ValueTaskMiss() | 1136 | 1136 | - | - | - | - | ok |
| GeneratedContractBenchmarks.TaskMiss() | 1128 | 1128 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=False) | 2552.1 | 2552.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=False) | 4112.1 | 4112.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=False) | 8256.2 | 8256.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7304.2 | 7304.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=True) | 2784.1 | 2784.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=True) | 4432.1 | 4432.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=True) | 8256.2 | 8256.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7305.7 | 7305 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=False) | 2552.1 | 2552.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=False) | 4112.1 | 4112.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=False) | 8256.2 | 8256.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7304.7 | 7304.4 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=True) | 2784.1 | 2784.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=True) | 4432.1 | 4432.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=True) | 8256.2 | 8256.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7305.9 | 7304.5 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7469.9 | 7469.9 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7470.3 | 7528.8 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7469.9 | 7469.9 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7470.4 | 7470.4 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=False) | 0.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=False) | 1877.3 | 1876.5 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=False) | 4.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=False) | 6933.1 | 6923.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=True) | 0.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=True) | 1898 | 1900.3 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=True) | 4.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=True) | 6964.2 | 6952.6 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=False) | 1679.9 | 1675.1 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=False) | 2764.6 | 2778.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=False) | 1685.8 | 1684.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=False) | 10420 | 10382.5 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=True) | 1680.8 | 1684.9 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=True) | 2795 | 2803.8 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=True) | 1688.5 | 1675 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=True) | 10468.5 | 10427.3 | - | - | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=False) | 6496.1 | 6496.1 | hget 1, hmget 2 | hget 1, hmget 2 | - | - | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=False) | 10345.6 | 10345.1 | expire 1, hget 1, hmget 2, hmset 1 | expire 1, hget 1, hmget 2, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=True) | 6496.8 | 6496.1 | hget 1, hmget 2 | hget 1, hmget 2 | - | - | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=True) | 10344.1 | 10344.1 | expire 1, hget 1, hmget 2, hmset 1 | expire 1, hget 1, hmget 2, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=False) | 16264.2 | 16264.2 | - | - | 2 (1070 ch) | 2.01 (1073 ch) | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=False) | 25194 | 25174.8 | - | - | 3 (1774 ch) | 3 (1774 ch) | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=True) | 16168.2 | 16168.2 | - | - | 2 (1073 ch) | 2 (1070 ch) | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=True) | 25194 | 25173.6 | - | - | 3 (1774 ch) | 3 (1774 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=False) | 2544.1 | 2544.1 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=False) | 7056.2 | 7056.2 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=True) | 1792 | 1792 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=True) | 1944 | 1944 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=False) | 8392.1 | 8392.1 | hmget 2 | hmget 2 | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=False) | 15698.1 | 15670.9 | expire 1.58, hmget 3, hmset 1.58 | expire 1.62, hmget 3, hmset 1.62 | - | - | accepted |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=True) | 1792 | 1792 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=True) | 1944 | 1944 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=False) | 18818.5 | 18818.3 | - | - | 2 (1090 ch) | 2 (1090 ch) | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=False) | 40938 | 40938 | - | - | 4.95 (3027 ch) | 4.95 (3027 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=True) | 1792 | 1792 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=True) | 1944 | 1944 | - | - | - | - | ok |

- DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=False): Redis commands 6.16 -> 6.24 (accepted: Same generation vector as a fresh hit (3 EVALSHA, each HMGET + TTL). A miss in a URL slot nobody has used yet also creates that slot's epoch (HSET + PERSIST), a share that depends on how many new slots the run reaches.)

