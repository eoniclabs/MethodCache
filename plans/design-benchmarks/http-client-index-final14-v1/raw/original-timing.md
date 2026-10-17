| Case | Base ns | Head ns | Head/base | 95 % interval | Verdict |
|---|---:|---:|---:|---|---|
| DesignHttpWorkloadBenchmarks.Miss(Backend=Local,KeepCookies=False) | 3579.5 | 3770.0 | 1.051 | 1.045-1.058 | **slower** |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 5496.8 | 4308.6 | 0.786 | 0.778-0.793 | faster |
| HttpColdMissBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 5376.2 | 4386.1 | 0.867 | 0.806-0.933 | **too noisy** |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 5451.3 | 4317.2 | 0.787 | 0.778-0.797 | faster |
| HttpColdMissBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 5296.4 | 4327.5 | 0.850 | 0.779-0.927 | **too noisy** |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=False) | 6799.7 | 3822.6 | 0.560 | 0.549-0.571 | faster |
| HttpResponsePathBenchmarks.Miss(Listening=False,AfterInvalidation=True) | 6795.5 | 3811.5 | 0.559 | 0.555-0.564 | faster |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=False) | 6829.0 | 3831.8 | 0.568 | 0.547-0.590 | faster |
| HttpResponsePathBenchmarks.Miss(Listening=True,AfterInvalidation=True) | 6794.0 | 3855.9 | 0.572 | 0.562-0.582 | faster |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=False) | 2044.1 | 2103.3 | 1.030 | 1.021-1.039 | same |
| HttpResponsePathBenchmarks.Revalidated(Listening=False,AfterInvalidation=True) | 2063.9 | 2108.0 | 1.023 | 1.015-1.031 | same |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=False) | 2056.3 | 2132.0 | 1.041 | 1.032-1.049 | **slower** |
| HttpResponsePathBenchmarks.Revalidated(Listening=True,AfterInvalidation=True) | 2050.4 | 2159.0 | 1.067 | 1.040-1.095 | **slower** |
| HttpResponsePathBenchmarks.VariantHit(Listening=True,AfterInvalidation=False) | 1279.2 | 1228.5 | 0.960 | 0.954-0.967 | faster |
