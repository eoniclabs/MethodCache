| Case | Bytes base | Bytes head | Redis base | Redis head | SQL base | SQL head | Gate |
|---|---:|---:|---|---|---:|---:|---|
| GeneratedContractBenchmarks.SyncHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.GenericHit() | 224.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.TaskHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.ValueTaskHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.StringHit() | 0.2 | 0.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.RecordHit() | 296.2 | 296.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.CollectionHit() | 1672.2 | 896.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.FallbackHit() | 408.2 | 320.2 | - | - | - | - | ok |
| GeneratedContractBenchmarks.SyncMiss() | 1080 | 984 | - | - | - | - | ok |
| GeneratedContractBenchmarks.ValueTaskMiss() | 1088 | 1088 | - | - | - | - | ok |
| GeneratedContractBenchmarks.TaskMiss() | 1080 | 1080 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=False) | 2552.1 | 2112.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=False) | 4080.1 | 3160.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=False) | 8232.3 | 7010.4 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7280.2 | 6953.6 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=True) | 2784.1 | 2520.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=True) | 4400.1 | 3720.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=True) | 8232.2 | 7008 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7280.7 | 6967.8 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=False) | 2552.1 | 2112.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=False) | 4080.1 | 3160.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=False) | 8232.2 | 7280 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7280.2 | 7210.7 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=True) | 2784.1 | 2520.2 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=True) | 4400.1 | 3720.5 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=True) | 8232.2 | 7280 | - | - | - | - | ok |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7280.5 | 7228.3 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7477.1 | 7753.9 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7446.4 | 7849.2 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7445.9 | 8055.1 | - | - | - | - | ok |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7446.3 | 8050.1 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=False) | 504.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=False) | 2327.1 | 1828.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=False) | 508.9 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=False) | 9617.5 | 6806.6 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=True) | 504.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=True) | 2350.5 | 1849.9 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=True) | 508.8 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=True) | 9651.9 | 6826.5 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=False) | 504.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=False) | 2883.9 | 2028.5 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=False) | 508.9 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=False) | 12011 | 7000.8 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=True) | 504.2 | 0.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=True) | 2902.8 | 2054.2 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=True) | 508.8 | 4.7 | - | - | - | - | ok |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=True) | 12062.1 | 7043.3 | - | - | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=False) | 5360 | 5216.1 | hget 1, hmget 1 | hget 2 | - | - | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=False) | 10641.1 | 10464.1 | expire 1, hget 1, hmget 2, hmset 1 | expire 1, hget 2, hmget 1, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=True) | 5360.4 | 5216 | hget 1, hmget 1 | hget 2 | - | - | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=True) | 10640.1 | 10952.1 | expire 1, hget 1, hmget 2, hmset 1 | expire 1, hget 2, hmget 1, hmset 1 | - | - | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=False) | 10120.1 | 9904.1 | - | - | 1 (538 ch) | 1 (192 ch) | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=False) | 25482 | 24242 | - | - | 3 (1774 ch) | 3 (1431 ch) | ok |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=True) | 10072.7 | 9856.1 | - | - | 1 (538 ch) | 1 (192 ch) | ok |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=True) | 25482 | 24730 | - | - | 3 (1774 ch) | 3 (1431 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=False) | 2544.1 | 2112.2 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=False) | 7032.2 | 6763.6 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=True) | 1792 | 1912 | - | - | - | - | accepted |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=True) | 1944 | 2064 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=False) | 8376.1 | 14440.1 | hmget 2 | evalsha 3, hmget 4, ttl 3 | - | - | accepted |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=False) | 15592.4 | 25157 | expire 1.63, hmget 3, hmset 1.63 | evalsha 3, expire 1, hmget 5, hmset 1, hset 0.54, persist 0.54, ttl 2.46 | - | - | accepted |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=True) | 1792 | 1912 | - | - | - | - | accepted |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=True) | 1944 | 2064 | - | - | - | - | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=False) | 18802.2 | 24009.9 | - | - | 2 (1090 ch) | 2 (5265 ch) | accepted |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=False) | 40858 | 47450 | - | - | 4.95 (3027 ch) | 4 (6524 ch) | ok |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=True) | 1792 | 1912 | - | - | - | - | accepted |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=True) | 1944 | 2064 | - | - | - | - | ok |

- DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=True): hit bytes 1792 -> 1912 (accepted: Design spec 9.3: with KeepCookies the handler stays in the pipeline and bypasses the cache. f43854d had no KeepCookies, so its baseline runs without the handler. The bypass reads the request's Cache-Control for only-if-cached (48 B of request headers) and returns a completed Task (72 B).)
- DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=False): Redis commands 2.00 -> 10.00 (accepted: Design spec 9.2: every HTTP L2 lookup reads the shared generation vector (global clear, URL slot, tags) before the representation, so invalidation by another node applies at once. Each component is a read-only Lua script on one key (cluster-safe), so 3 EVALSHA, each HMGET + TTL, pipelined in one round trip, then the entry HMGET. Nothing is written. The bytes are those scripts and the bounded transport (timeout and breaker) around both reads.)
- DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=False): hit bytes 8376.1 -> 14440.1 (accepted: Design spec 9.2: every HTTP L2 lookup reads the shared generation vector (global clear, URL slot, tags) before the representation, so invalidation by another node applies at once. Each component is a read-only Lua script on one key (cluster-safe), so 3 EVALSHA, each HMGET + TTL, pipelined in one round trip, then the entry HMGET. Nothing is written. The bytes are those scripts and the bounded transport (timeout and breaker) around both reads.)
- DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=False): Redis commands 6.26 -> 13.54 (accepted: Same generation vector as a fresh hit (3 EVALSHA, each HMGET + TTL). A miss in a URL slot nobody has used yet also creates that slot's epoch (HSET + PERSIST), a share that depends on how many new slots the run reaches.)
- DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=True): hit bytes 1792 -> 1912 (accepted: Design spec 9.3: with KeepCookies the handler stays in the pipeline and bypasses the cache. f43854d had no KeepCookies, so its baseline runs without the handler. The bypass reads the request's Cache-Control for only-if-cached (48 B of request headers) and returns a completed Task (72 B).)
- DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=False): hit bytes 18802.2 -> 24009.9 (accepted: Design spec 9.2: the generation vector is one parameterized SELECT without locks once the epochs exist, then the entry read, so still 2 commands per hit. The extra bytes are SqlClient's cost for that command with 4 parameters and the bounded transport around both reads.)
- DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=True): hit bytes 1792 -> 1912 (accepted: Design spec 9.3: with KeepCookies the handler stays in the pipeline and bypasses the cache. f43854d had no KeepCookies, so its baseline runs without the handler. The bypass reads the request's Cache-Control for only-if-cached (48 B of request headers) and returns a completed Task (72 B).)
