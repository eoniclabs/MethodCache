| Case | Base ns | Head ns | Head/base | 95 % interval | Verdict |
|---|---:|---:|---:|---|---|
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=False) | 2024.5 | 1762.0 | 1.023 | 0.851-1.229 | **too noisy** |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Local,KeepCookies=True) | 634.2 | 705.7 | 1.104 | 1.089-1.120 | **slower** |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=False) | 225838.5 | 367903.0 | 1.559 | 1.408-1.726 | **too noisy** |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=Redis,KeepCookies=True) | 642.2 | 706.7 | 1.098 | 1.083-1.113 | **slower** |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=False) | 504158.9 | 605176.4 | 1.223 | 1.090-1.373 | **too noisy** |
| DesignHttpWorkloadBenchmarks.Fresh(Backend=SqlServer,KeepCookies=True) | 634.1 | 695.3 | 1.110 | 1.078-1.143 | **too noisy** |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=False) | 7543.5 | 7658.1 | 1.024 | 0.823-1.274 | **too noisy** |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=True) | 675.5 | 738.2 | 1.096 | 1.079-1.114 | **slower** |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=False) | 543535.7 | 644450.5 | 1.206 | 1.125-1.293 | **too noisy** |
| DesignHttpWorkloadBenchmarks.Miss(Backend=Redis,KeepCookies=True) | 676.1 | 748.1 | 1.109 | 1.093-1.126 | **slower** |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=False) | 23267888.2 | 22765315.3 | 0.992 | 0.936-1.052 | **too noisy** |
| DesignHttpWorkloadBenchmarks.Miss(Backend=SqlServer,KeepCookies=True) | 675.6 | 729.0 | 1.088 | 1.069-1.108 | **slower** |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=False) | 91979.6 | 106180.3 | 1.108 | 0.993-1.237 | **too noisy** |
| DesignL2WorkloadBenchmarks.Read(Backend=Redis,RoundTrip=True) | 97287.7 | 102037.2 | 1.072 | 0.991-1.158 | **too noisy** |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=False) | 222133.6 | 199838.0 | 0.917 | 0.880-0.956 | **too noisy** |
| DesignL2WorkloadBenchmarks.Read(Backend=SqlServer,RoundTrip=True) | 208855.3 | 196661.7 | 0.958 | 0.896-1.024 | **too noisy** |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=False) | 288788.2 | 297553.2 | 1.033 | 0.950-1.124 | **too noisy** |
| DesignL2WorkloadBenchmarks.Write(Backend=Redis,RoundTrip=True) | 278425.3 | 290308.6 | 1.039 | 0.976-1.107 | **too noisy** |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=False) | 11013474.3 | 11622205.9 | 1.049 | 1.002-1.098 | **too noisy** |
| DesignL2WorkloadBenchmarks.Write(Backend=SqlServer,RoundTrip=True) | 11441187.6 | 11219506.8 | 0.990 | 0.945-1.037 | **too noisy** |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=False) | 78.2 | 27.3 | 0.355 | 0.336-0.376 | faster |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=False,Weighted=True) | 76.7 | 28.3 | 0.373 | 0.350-0.397 | faster |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=False) | 86.3 | 30.0 | 0.351 | 0.329-0.375 | faster |
| DesignMemoryWorkloadBenchmarks.ContendedHits(Listening=True,Weighted=True) | 87.2 | 29.0 | 0.350 | 0.327-0.374 | faster |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=False) | 1094.2 | 1009.5 | 0.922 | 0.898-0.948 | faster |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=False,Weighted=True) | 1068.6 | 994.8 | 0.928 | 0.904-0.952 | faster |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=False) | 1356.7 | 1138.4 | 0.839 | 0.823-0.855 | faster |
| DesignMemoryWorkloadBenchmarks.EvictionPressure(Listening=True,Weighted=True) | 1355.3 | 1086.3 | 0.797 | 0.784-0.811 | faster |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=False) | 105.1 | 41.0 | 0.378 | 0.357-0.401 | faster |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=False,Weighted=True) | 102.6 | 41.5 | 0.390 | 0.365-0.416 | faster |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=False) | 111.6 | 47.2 | 0.419 | 0.384-0.456 | **too noisy** |
| DesignMemoryWorkloadBenchmarks.Hit(Listening=True,Weighted=True) | 111.2 | 47.3 | 0.427 | 0.402-0.454 | faster |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=False) | 8617.1 | 7388.1 | 0.870 | 0.830-0.911 | **too noisy** |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=False,Weighted=True) | 8523.5 | 7357.2 | 0.860 | 0.830-0.891 | **too noisy** |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=False) | 11585.9 | 7726.3 | 0.684 | 0.634-0.738 | **too noisy** |
| DesignMemoryWorkloadBenchmarks.SingleFlightBatch(Listening=True,Weighted=True) | 11210.5 | 7370.2 | 0.664 | 0.625-0.707 | **too noisy** |
| GeneratedContractBenchmarks.CollectionHit() | 285.2 | 200.9 | 0.703 | 0.688-0.718 | faster |
| GeneratedContractBenchmarks.FallbackHit() | 552.2 | 698.5 | 1.161 | 0.871-1.547 | **too noisy** |
| GeneratedContractBenchmarks.GenericHit() | 92.2 | 72.7 | 0.787 | 0.766-0.809 | faster |
| GeneratedContractBenchmarks.RecordHit() | 139.6 | 138.7 | 0.997 | 0.980-1.015 | same |
| GeneratedContractBenchmarks.StringHit() | 61.4 | 64.1 | 1.048 | 1.018-1.079 | **too noisy** |
| GeneratedContractBenchmarks.SyncHit() | 55.9 | 61.3 | 1.089 | 1.071-1.108 | **slower** |
| GeneratedContractBenchmarks.SyncMiss() | 793.8 | 820.4 | 1.021 | 1.001-1.042 | same |
| GeneratedContractBenchmarks.TaskHit() | 61.2 | 68.8 | 1.119 | 1.074-1.166 | **too noisy** |
| GeneratedContractBenchmarks.TaskMiss() | 816.2 | 889.0 | 1.106 | 1.081-1.132 | **slower** |
| GeneratedContractBenchmarks.ValueTaskHit() | 66.9 | 72.0 | 1.098 | 1.058-1.140 | **too noisy** |
| GeneratedContractBenchmarks.ValueTaskMiss() | 812.1 | 907.8 | 1.118 | 1.097-1.138 | **slower** |
| GeneratedPrefixHitPathBenchmarks.SyncHit(LongPrefix=False,Listening=False,ArgumentKind=Int) | 62.8 | 55.1 | 0.885 | 0.856-0.916 | **too noisy** |
| GeneratedPrefixHitPathBenchmarks.SyncHit(LongPrefix=False,Listening=True,ArgumentKind=Int) | 81.5 | 58.1 | 0.712 | 0.687-0.739 | faster |
| GeneratedPrefixHitPathBenchmarks.SyncHit(LongPrefix=True,Listening=False,ArgumentKind=Int) | 168.3 | 120.4 | 0.714 | 0.708-0.721 | faster |
| GeneratedPrefixHitPathBenchmarks.SyncHit(LongPrefix=True,Listening=True,ArgumentKind=Int) | 182.7 | 123.4 | 0.672 | 0.660-0.684 | faster |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7840.1 | 11143.7 | 1.413 | 1.382-1.445 | **too noisy** |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7930.1 | 11278.0 | 1.382 | 1.338-1.427 | **too noisy** |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7954.2 | 11212.2 | 1.420 | 1.383-1.459 | **too noisy** |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7951.0 | 11234.3 | 1.395 | 1.352-1.440 | **too noisy** |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=False) | 2038.3 | 2211.6 | 1.061 | 0.940-1.197 | **too noisy** |
| HttpResponsePathBenchmarks.FreshHit(Listening=False,AfterInvalidation=True) | 2087.0 | 2291.2 | 1.136 | 0.974-1.324 | **too noisy** |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=False) | 2062.5 | 2170.3 | 1.136 | 0.898-1.438 | **too noisy** |
| HttpResponsePathBenchmarks.FreshHit(Listening=True,AfterInvalidation=True) | 2062.1 | 2065.8 | 1.072 | 0.896-1.282 | **too noisy** |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7782.1 | 7853.8 | 1.170 | 0.965-1.418 | **too noisy** |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 7725.1 | 7828.0 | 1.094 | 0.914-1.310 | **too noisy** |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 7707.7 | 8077.7 | 1.249 | 0.897-1.739 | **too noisy** |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 7795.0 | 8335.2 | 1.367 | 1.016-1.839 | **too noisy** |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=False) | 5269.7 | 5444.9 | 1.340 | 0.973-1.845 | **too noisy** |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=True) | 5353.0 | 5424.1 | 0.976 | 0.680-1.401 | **too noisy** |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=False) | 5336.4 | 5706.4 | 1.300 | 0.919-1.840 | **too noisy** |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=True) | 5333.5 | 5558.3 | 1.395 | 0.984-1.978 | **too noisy** |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=False) | 3771.6 | 3964.9 | 1.054 | 0.870-1.276 | **too noisy** |
| HttpResponsePathBenchmarks.VariantHit(Listening=False,AfterInvalidation=True) | 3807.4 | 3171.1 | 0.926 | 0.816-1.051 | **too noisy** |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=False) | 3697.9 | 3486.9 | 1.012 | 0.855-1.199 | **too noisy** |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=True) | 3792.4 | 3214.7 | 1.011 | 0.775-1.318 | **too noisy** |
