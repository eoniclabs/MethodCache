| Case | Bytes base | Bytes head | Redis base | Redis head | SQL base | SQL head | Gate |
|---|---:|---:|---|---|---:|---:|---|
| GeneratedContractBenchmarks.SyncHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.GenericHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.TaskHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.ValueTaskHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.StringHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.RecordHit() | 296.2 | 296.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.CollectionHit() | 896.2 | 896.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.FallbackHit() | 320.2 | 320.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.SyncMiss() | 984 | 984 | - | - | - | - | ok |
| GeneratedContractBenchmarks.ValueTaskMiss() | 1136 | 1136 | - | - | - | - | ok |
| GeneratedContractBenchmarks.TaskMiss() | 1128 | 1128 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=False) | 2200.2 | 2200.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=False) | 3248.5 | 3248.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=False) | 7168.6 | 7168 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7629.6 | 7607.3 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=True) | 2608.2 | 2608.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=True) | 3808.5 | 3808.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=True) | 7168 | 7168 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7622.2 | 7619.7 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=False) | 2200.2 | 2200.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=False) | 3248.5 | 3248.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=False) | 7368 | 7368 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7808.9 | 7800.6 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=True) | 2608.2 | 2608.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=True) | 3808.5 | 3808.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=True) | 7368 | 7368 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7808.6 | 7826.5 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 8423.4 | 8460.3 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 8529.2 | 8532.9 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 8688.3 | 8697.9 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 8860.1 | 8717.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=False) | 0.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=False) | 1876.4 | 1877 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=False) | 4.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=False) | 6964.9 | 6952.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=True) | 0.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=True) | 1899.3 | 1901.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=True) | 4.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=True) | 6987.3 | 6978 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=False) | 0.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=False) | 2080.7 | 2081.3 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=False) | 4.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=False) | 7157.7 | 7154.9 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=True) | 0.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=True) | 2101.5 | 2105.5 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=True) | 4.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=True) | 7196.6 | 7190.6 | - | - | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=False) | 5688.1 | 5688 | hget 2 | hget 2 | - | - | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=False) | 10938.7 | 10937.1 | expire 1, hget 2, hmget 1, hmset 1 | expire 1, hget 2, hmget 1, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=True) | 5688.4 | 5688.4 | hget 2 | hget 2 | - | - | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=True) | 11425 | 11425 | expire 1, hget 2, hmget 1, hmset 1 | expire 1, hget 2, hmget 1, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=False) | 11128.1 | 11128.1 | - | - | 1 (157 ch) | 1 (160 ch) | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=False) | 26034 | 26034 | - | - | 3 (1396 ch) | 3 (1396 ch) | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=True) | 11080.1 | 11080.1 | - | - | 1 (157 ch) | 1 (157 ch) | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=True) | 26522 | 26522 | - | - | 3 (1396 ch) | 3 (1396 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=False) | 2200.2 | 2200.2 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=False) | 7415 | 7419.8 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=True) | 2072 | 2072 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=True) | 2224 | 2224 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=False) | 15696.7 | 15696.7 | evalsha 3, hget 3, hmget 1, hset 3, persist 3 | evalsha 3, hget 3, hmget 1, hset 3, persist 3 | - | - | FAIL |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=False) | 26981.4 | 26981.8 | evalsha 3, expire 1, hget 3, hmget 2, hmset 1, hset 3, persist 3 | evalsha 3, expire 1, hget 3, hmget 2, hmset 1, hset 3, persist 3 | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=True) | 2072 | 2072 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=True) | 2224 | 2224 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=False) | 46138 | 46138 | ping 0.05 | - | 2 (3919 ch) | 2 (3919 ch) | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=False) | 67690.4 | 67771.6 | - | - | 4 (5178 ch) | 4 (5178 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=True) | 2072 | 2072 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=True) | 2224 | 2224 | - | - | - | - | ok |


- **DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=False): Redis writes on a hit: 6.00**
