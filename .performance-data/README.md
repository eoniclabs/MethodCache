# Performance Data Directory

This directory contains historical performance benchmark data for the MethodCache library.

## Structure

- `schema.json` - JSON schema for benchmark data format
- `benchmark-YYYYMMDD_HHMMSS.json` - Performance data files (one per benchmark run). Note: No benchmark data files are currently present in this directory.
- `generate-charts.py` - Script to generate performance charts

## Data Format

Each benchmark data file follows this structure:

```json
{
  "metadata": {
    "version": "v1.2.3",
    "commit": "abc123...",
    "branch": "main",
    "timestamp": "2024-01-01T12:00:00Z",
    "environment": {
      "os": "ubuntu-latest",
      "dotnet": "9.0.x"
    }
  },
  "benchmarks": [
    {
      "name": "BasicCachingBenchmarks.CacheHit",
      "method": "CacheHit",
      "parameters": {
        "DataSize": 1,
        "ModelType": "Small"
      },
      "statistics": {
        "mean": 123.45,
        "error": 1.23,
        "stdDev": 2.34,
        "median": 120.00,
        "min": 115.00,
        "max": 130.00
      },
      "memory": {
        "allocated": 1024,
        "gen0": 0,
        "gen1": 0,
        "gen2": 0
      }
    }
  ]
}
```

## Usage

Performance data is automatically collected by GitHub Actions on:
- Every push to main branch
- Every pull request
- Weekly scheduled runs
- Manual workflow dispatch

The data is used to:
- Generate performance reports in PERFORMANCE.md
- Detect performance regressions in PRs
- Track performance trends over time
- Create visualizations for the README

## Retention

Performance data files are kept indefinitely in the repository to maintain historical trends. Large files are compressed and archived monthly.