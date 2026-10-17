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
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7304.5 | 7304.4 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=False) | 2552.1 | 2552.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=False) | 4112.1 | 4112.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=False) | 8256.2 | 8256.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7304.4 | 7304.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=True) | 2784.1 | 2784.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=True) | 4432.1 | 4432.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=True) | 8256.2 | 8256.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7304.5 | 7304.9 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7469.9 | 7469.9 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7470.3 | 7470.3 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7469.9 | 7469.9 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7470.3 | 7470.3 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=False) | 504.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=False) | 2375 | 1876.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=False) | 508.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=False) | 9844.4 | 6929.3 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=True) | 504.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=True) | 2398.1 | 1899.5 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=True) | 508.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=True) | 9884 | 6957.3 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=False) | 504.2 | 1681.5 | - | - | - | - | FAIL |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=False) | 2932.4 | 2775.6 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=False) | 508.7 | 1681.2 | - | - | - | - | FAIL |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=False) | 12249.2 | 10388.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=True) | 504.2 | 1675 | - | - | - | - | FAIL |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=True) | 2957.2 | 2800.3 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=True) | 508.7 | 1674.3 | - | - | - | - | FAIL |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=True) | 12295.6 | 10417.3 | - | - | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=False) | 5416 | 6496.1 | hget 1, hmget 1 | hget 1, hmget 2 | - | - | FAIL |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=False) | 10697.2 | 10344.1 | expire 1, hget 1, hmget 2, hmset 1 | expire 1, hget 1, hmget 2, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=True) | 5416 | 6496.1 | hget 1, hmget 1 | hget 1, hmget 2 | - | - | FAIL |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=True) | 10696.1 | 10344.1 | expire 1, hget 1, hmget 2, hmset 1 | expire 1, hget 1, hmget 2, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=False) | 10151.5 | 16264.2 | - | - | 1 (535 ch) | 2 (1070 ch) | FAIL |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=False) | 25538 | 25194 | - | - | 3 (1774 ch) | 3 (1774 ch) | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=True) | 10128.8 | 16168.2 | - | - | 1 (538 ch) | 2 (1073 ch) | FAIL |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=True) | 25515.2 | 25173.6 | - | - | 3 (1774 ch) | 3 (1774 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=False) | 2544.1 | 2544.1 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=False) | 7056.2 | 7056.2 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=True) | 1792 | 1792 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=True) | 1944 | 1944 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=False) | 8392.1 | 8392.5 | hmget 2 | hmget 2 | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=False) | 15722.1 | 15698.1 | expire 1.57, hmget 3, hmset 1.57 | expire 1.58, hmget 3, hmset 1.58 | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=True) | 1792 | 1792 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=True) | 1944 | 1944 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=False) | 18818.3 | 18818.1 | ping 0 | - | 2 (1090 ch) | 2 (1090 ch) | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=False) | 40938 | 40946.8 | - | - | 4.95 (3027 ch) | 4.95 (3027 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=True) | 1792 | 1792 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=True) | 1944 | 1944 | - | - | - | - | ok |


- **DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=False): hit bytes 504.2 -> 1681.5**
- **DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=False): hit bytes 508.7 -> 1681.2**
- **DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=True): hit bytes 504.2 -> 1675**
- **DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=True): hit bytes 508.7 -> 1674.3**
- **DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=False): Redis commands 2.00 -> 3.00**
- **DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=True): Redis commands 2.00 -> 3.00**
- **DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=False): SQL commands 1 -> 2**
- **DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=True): SQL commands 1 -> 2**
