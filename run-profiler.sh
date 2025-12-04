#!/bin/bash
cd MethodCache.Benchmarks
export BENCHMARK_QUICK=true
dotnet run -c Release --project MethodCache.Benchmarks.csproj -- MethodCache.Benchmarks.Microbenchmarks.SourceGenSyncPathProfiler
