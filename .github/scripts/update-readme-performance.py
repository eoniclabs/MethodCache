#!/usr/bin/env python3
"""
README Performance Section Updater

Updates the performance section in README.md with latest benchmark data
"""

import json
import glob
import re
import argparse
from datetime import datetime
from typing import Optional

# Configuration constants
DEFAULT_DATA_SIZE = 1
DEFAULT_MODEL_TYPE = 'Small'
BENCHMARK_PARAMETERS = {
    'DataSize': DEFAULT_DATA_SIZE,
    'ModelType': DEFAULT_MODEL_TYPE
}


def load_latest_performance_data() -> Optional[dict]:
    """Load the most recent performance data"""
    data_files = sorted(glob.glob('.performance-data/benchmark-*.json'), reverse=True)

    if not data_files:
        return None

    try:
        with open(data_files[0], 'r') as f:
            return json.load(f)
    except (IOError, json.JSONDecodeError) as e:
        print(f"Error loading performance data: {e}")
        return None


def generate_performance_badges(data: dict) -> str:
    """Generate performance badges for README"""
    badges = []

    # Find key benchmark results
    cache_hit_small = None
    cache_miss_small = None

    for benchmark in data['benchmarks']:
        params = benchmark['parameters']
        if (params.get('DataSize') == BENCHMARK_PARAMETERS['DataSize'] and
            params.get('ModelType') == BENCHMARK_PARAMETERS['ModelType']):
            if benchmark['method'] == 'CacheHit':
                cache_hit_small = benchmark['statistics']['mean']
            elif benchmark['method'] == 'CacheMiss':
                cache_miss_small = benchmark['statistics']['mean']

    # Cache Hit Performance Badge
    if cache_hit_small is not None:
        if cache_hit_small < 1000:
            hit_label = f"{cache_hit_small:.0f}ns"
            color = "brightgreen"
        elif cache_hit_small < 10000:
            hit_label = f"{cache_hit_small/1000:.1f}μs"
            color = "green"
        else:
            hit_label = f"{cache_hit_small/1000:.0f}μs"
            color = "yellow"

        badges.append(f"![Cache Hit Performance](https://img.shields.io/badge/Cache%20Hit-{hit_label}-{color})")

    # Cache Miss Performance Badge
    if cache_miss_small is not None:
        if cache_miss_small < 1000000:  # < 1ms
            miss_label = f"{cache_miss_small/1000:.0f}μs"
            color = "green"
        elif cache_miss_small < 10000000:  # < 10ms
            miss_label = f"{cache_miss_small/1000000:.1f}ms"
            color = "yellow"
        else:
            miss_label = f"{cache_miss_small/1000000:.0f}ms"
            color = "orange"

        badges.append(f"![Cache Miss Performance](https://img.shields.io/badge/Cache%20Miss-{miss_label}-{color})")

    # Version Badge
    version = data['metadata']['version']
    badges.append(f"![Benchmark Version](https://img.shields.io/badge/Benchmarked-{version}-blue)")

    return " ".join(badges)


def generate_performance_table(data: dict) -> str:
    """Generate performance comparison table"""
    # Group benchmarks by method
    methods = {}
    for benchmark in data['benchmarks']:
        method = benchmark['method']
        if method not in methods:
            methods[method] = []
        methods[method].append(benchmark)

    # Generate table
    table = []
    table.append("| Operation | Small Model (1 item) | Medium Model (1 item) | Large Model (1 item) |")
    table.append("|-----------|---------------------|----------------------|---------------------|")

    # Key methods to include in README
    key_methods = ['NoCaching', 'CacheMiss', 'CacheHit', 'CacheHitCold', 'CacheInvalidation']

    for method in key_methods:
        if method not in methods:
            continue

        benchmarks = methods[method]
        row = [method.replace('No', 'No ').replace('Cache', 'Cache ')]

        for model_type in ['Small', 'Medium', 'Large']:
            # Find benchmark for this model type with DataSize=1
            found = None
            for b in benchmarks:
                if (b['parameters'].get('DataSize') == 1 and
                    b['parameters'].get('ModelType') == model_type):
                    found = b
                    break

            if found and found['statistics']['mean'] > 0:
                mean_ns = found['statistics']['mean']
                if mean_ns < 1000:
                    row.append(f"**{mean_ns:.0f} ns**")
                elif mean_ns < 1000000:
                    row.append(f"**{mean_ns/1000:.1f} μs**")
                else:
                    row.append(f"**{mean_ns/1000000:.1f} ms**")
            else:
                row.append("N/A")

        table.append("| " + " | ".join(row) + " |")

    return "\n".join(table)


def generate_performance_section(data: dict) -> str:
    """Generate the complete performance section for README"""
    badges = generate_performance_badges(data)
    table = generate_performance_table(data)

    # Calculate cache speedup
    cache_hit_time = None
    no_cache_time = None

    for benchmark in data['benchmarks']:
        params = benchmark['parameters']
        if (params.get('DataSize') == BENCHMARK_PARAMETERS['DataSize'] and
            params.get('ModelType') == BENCHMARK_PARAMETERS['ModelType']):
            if benchmark['method'] == 'CacheHit':
                cache_hit_time = benchmark['statistics']['mean']
            elif benchmark['method'] == 'NoCaching':
                no_cache_time = benchmark['statistics']['mean']

    speedup_text = ""
    if cache_hit_time and no_cache_time and cache_hit_time > 0:
        speedup = no_cache_time / cache_hit_time
        speedup_text = f"\n🚀 **Cache speedup: {speedup:.0f}x faster** than no caching"

    timestamp = data['metadata']['timestamp']
    date = datetime.fromisoformat(timestamp.replace('Z', '+00:00')).strftime('%B %d, %Y')

    return f"""## ⚡ Performance

{badges}

MethodCache delivers exceptional performance with microsecond-level cache hits:{speedup_text}

{table}

> 📊 **Benchmarks** run on .NET 9.0 with BenchmarkDotNet. Results from {date}.
>
> 📈 [View detailed performance trends](PERFORMANCE.md) | 🔍 [Raw benchmark data](.performance-data/)

### Performance Highlights

- **Cache Hits**: Sub-microsecond response times for cached data
- **Memory Efficient**: Minimal memory allocations during cache operations
- **Scalable**: Consistent performance across different data sizes
- **Zero-Overhead**: Negligible impact when caching is disabled"""


def update_readme_performance(readme_path: str, performance_section: str) -> bool:
    """Update the performance section in README.md"""
    try:
        with open(readme_path, 'r', encoding='utf-8') as f:
            content = f.read()

        # Find and replace performance section
        # Look for section starting with "## ⚡ Performance" or "## Performance"
        pattern = r'(## ⚡ Performance|## Performance).*?(?=\n## |\n# |\Z)'
        replacement = performance_section

        if re.search(pattern, content, re.DOTALL):
            new_content = re.sub(pattern, replacement, content, flags=re.DOTALL)
        else:
            # If no performance section exists, add it before the last section
            # Find a good insertion point (before Contributing, License, etc.)
            insertion_patterns = [
                r'\n(## Contributing)',
                r'\n(## License)',
                r'\n(## Support)',
                r'\n(## Documentation)',
                r'\Z'  # End of file
            ]

            inserted = False
            for pattern in insertion_patterns:
                if re.search(pattern, content):
                    new_content = re.sub(pattern, f'\n{performance_section}\n\n\\1', content, count=1)
                    inserted = True
                    break

            if not inserted:
                new_content = content + f'\n\n{performance_section}\n'

        with open(readme_path, 'w', encoding='utf-8') as f:
            f.write(new_content)

        return True

    except IOError as e:
        print(f"Error updating README: {e}")
        return False


def main():
    parser = argparse.ArgumentParser(description='Update README performance section')
    parser.add_argument('--readme', default='README.md', help='Path to README.md file')
    parser.add_argument('--dry-run', action='store_true', help='Print what would be updated without changing files')
    args = parser.parse_args()

    print("Loading latest performance data...")
    data = load_latest_performance_data()

    if not data:
        print("No performance data found!")
        return 1

    print(f"Using performance data from {data['metadata']['timestamp']}")

    performance_section = generate_performance_section(data)

    if args.dry_run:
        print("\n" + "="*80)
        print("Performance section that would be added to README:")
        print("="*80)
        print(performance_section)
        print("="*80)
    else:
        print(f"Updating {args.readme}...")
        success = update_readme_performance(args.readme, performance_section)

        if success:
            print("✅ README updated successfully!")
        else:
            print("❌ Failed to update README")
            return 1

    return 0


if __name__ == '__main__':
    exit(main())