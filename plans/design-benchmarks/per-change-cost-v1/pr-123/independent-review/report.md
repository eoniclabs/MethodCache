| Case | Bytes base | Bytes head | Redis base | Redis head | SQL base | SQL head | Gate |
|---|---:|---:|---|---|---:|---:|---|
| GeneratedContractBenchmarks.SyncHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.GenericHit() | 224.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.TaskHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.ValueTaskHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.StringHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.RecordHit() | 296.2 | 296.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.CollectionHit() | 1672.2 | 896.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.FallbackHit() | 408.2 | 408.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.SyncMiss() | 1104 | 984 | - | - | - | - | ok |
| GeneratedContractBenchmarks.ValueTaskMiss() | 1112 | 1136 | - | - | - | - | ok |
| GeneratedContractBenchmarks.TaskMiss() | 1104 | 1128 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=False) | 2552.1 | 2552.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=False) | 4112.1 | 4112.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=False) | 8256.2 | 8256.3 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7304.2 | 7304.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=True) | 2784.1 | 2784.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=True) | 4432.1 | 4432.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=True) | 8256.2 | 8256.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7305.1 | 7304.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=False) | 2552.1 | 2552.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=False) | 4112.1 | 4112.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=False) | 8256.2 | 8256.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7305.1 | 7304.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=True) | 2784.1 | 2784.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=True) | 4432.1 | 4432.1 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=True) | 8256.2 | 8256.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7304.6 | 7304.5 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7462.4 | 7478.5 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7470.3 | 7470.1 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7469.9 | 7469.9 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7470.3 | 7470.3 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=False) | 504.2 | 504.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=False) | 2350.9 | 2375.4 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=False) | 508.9 | 508.8 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=False) | 9754.7 | 9851.1 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=True) | 504.2 | 504.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=True) | 2375.4 | 2400.8 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=True) | 508.7 | 508.9 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=True) | 9779.7 | 9883.1 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=False) | 504.2 | 504.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=False) | 2908.8 | 2929.3 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=False) | 508.8 | 508.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=False) | 12166.9 | 12260.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=True) | 504.2 | 504.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=True) | 2928 | 2957.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=True) | 508.7 | 508.8 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=True) | 12178.4 | 12261.5 | - | - | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=False) | 5392.1 | 5416.1 | hget 1, hmget 1 | hget 1, hmget 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=False) | 10673 | 10697.1 | expire 1, hget 1, hmget 2, hmset 1 | expire 1, hget 1, hmget 2, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=True) | 5392 | 5416.4 | hget 1, hmget 1 | hget 1, hmget 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=True) | 10672.1 | 10696.1 | expire 1, hget 1, hmget 2, hmset 1 | expire 1, hget 1, hmget 2, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=False) | 10152.1 | 10176.1 | - | - | 1 (535 ch) | 1 (535 ch) | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=False) | 25514 | 25538 | - | - | 3 (1774 ch) | 3 (1774 ch) | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=True) | 10104.8 | 10128.8 | - | - | 1 (535 ch) | 1 (535 ch) | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=True) | 25514 | 25518.8 | - | - | 3 (1774 ch) | 3 (1774 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=False) | 2544.1 | 2544.2 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=False) | 7056.2 | 7056.2 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=True) | 1792 | 1792 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=True) | 1944 | 1944 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=False) | 8392.5 | 8392.5 | hmget 2 | hmget 2 | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=False) | 15723.3 | 15686.2 | expire 1.56, hmget 3, hmset 1.56 | expire 1.58, hmget 3, hmset 1.58 | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=True) | 1792 | 1792 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=True) | 1944 | 1944 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=False) | 18818.4 | 18818.5 | - | - | 2 (1090 ch) | 2 (1090 ch) | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=False) | 40938 | 40936.4 | - | - | 4.95 (3027 ch) | 4.95 (3027 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=True) | 1792 | 1792 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=True) | 1944 | 1944 | - | - | - | - | ok |


