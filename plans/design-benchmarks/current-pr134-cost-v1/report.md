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
| GeneratedContractBenchmarks.ValueTaskMiss() | 1136 | 1088 | - | - | - | - | ok |
| GeneratedContractBenchmarks.TaskMiss() | 1128 | 1080 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=False) | 2200.2 | 2112.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=False) | 3248.5 | 3160.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=False) | 7536 | 7048 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7794.8 | 6578.8 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=True) | 2608.2 | 2520.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=True) | 3808.5 | 3720.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=True) | 7536 | 7048 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7765.3 | 6558 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=False) | 2200.2 | 2112.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=False) | 3248.5 | 3160.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=False) | 7736 | 7144 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7960.6 | 6651.7 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=True) | 2608.2 | 2520.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=True) | 3808.5 | 3720.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=True) | 7736 | 7144 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7958.1 | 6646.2 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 8582.9 | 6928.2 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 8600.5 | 6904.3 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 8871 | 7015.6 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 8837.3 | 6964.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=False) | 0.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=False) | 1875.2 | 1828 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=False) | 4.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=False) | 6966.1 | 6800.6 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=True) | 0.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=True) | 1899.4 | 1851 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=True) | 4.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=True) | 6989.8 | 6811.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=False) | 0.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=False) | 2071.7 | 1926.3 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=False) | 4.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=False) | 7174.4 | 6892.1 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=True) | 0.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=True) | 2101.3 | 1949.3 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=True) | 4.7 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=True) | 7176.7 | 6968 | - | - | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=False) | 5688.1 | 5216 | hget 2 | hget 2 | - | - | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=False) | 10937.1 | 10472.4 | expire 1, hget 2, hmget 1, hmset 1 | expire 1, hget 2, hmget 1, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=True) | 5688.4 | 5216.4 | hget 2 | hget 2, hmget 0 | - | - | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=True) | 11424.1 | 10953.1 | expire 1, hget 2, hmget 1, hmset 1 | expire 1, hget 2, hmget 1, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=False) | 11128.1 | 9901.9 | - | - | 1 (160 ch) | 1 (192 ch) | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=False) | 26034 | 24242 | - | - | 3 (1396 ch) | 3 (1431 ch) | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=True) | 11080.1 | 9856.1 | - | - | 1 (157 ch) | 1 (195 ch) | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=True) | 26522 | 24730 | - | - | 3 (1396 ch) | 3 (1431 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=False) | 2200.2 | 2112.2 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=False) | 7596.3 | 6589.6 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=True) | 2072 | 1912 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=True) | 2224 | 2064 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=False) | 15696.7 | 14516 | evalsha 3, hget 3, hmget 1, hset 3, persist 3 | evalsha 3, hmget 4, ttl 3 | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=False) | 27133.3 | 24569.3 | evalsha 3, expire 1, hget 3, hmget 2, hmset 1, hset 3, persist 3 | evalsha 3, expire 1, hmget 5, hmset 1, hset 0.56, persist 0.56, ttl 2.44 | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=True) | 2072 | 1912 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=True) | 2224 | 2064 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=False) | 46138 | 24081.7 | - | - | 2 (3919 ch) | 2 (5265 ch) | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=False) | 68072.4 | 46234.4 | - | - | 4 (5178 ch) | 4 (6524 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=True) | 2072 | 1912 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=True) | 2224 | 2064 | - | - | - | - | ok |


