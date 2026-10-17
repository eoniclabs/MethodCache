| Case | Base ns | Head ns | Head/base | 95 % interval | Verdict |
|---|---:|---:|---:|---|---|
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 7182.5 | 8737.3 | 1.211 | 1.184-1.238 | **slower** |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=True) | 5178.3 | 5586.0 | 1.100 | 1.044-1.159 | **too noisy** |
