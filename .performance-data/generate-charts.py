#!/usr/bin/env python3
"""
Performance Charts Generator

Generates SVG charts from benchmark data for performance visualization
"""

import json
import glob
import argparse
import sys
from datetime import datetime
from typing import List, Dict, Optional

def load_performance_data() -> List[Dict]:
    """Load all performance data files"""
    data_files = sorted(glob.glob('benchmark-*.json'), reverse=False)  # Oldest first

    data = []
    for file_path in data_files:
        try:
            with open(file_path, 'r') as f:
                file_data = json.load(f)
                data.append(file_data)
        except (IOError, json.JSONDecodeError) as e:
            print(f"Warning: Could not load {file_path}: {e}")

    return data

def generate_performance_trend_svg(data: List[Dict], method: str, output_path: str) -> bool:
    """Generate a simple SVG chart for performance trends"""
    if not data:
        return False

    # Extract data points for the specific method
    points = []
    for dataset in data:
        timestamp = dataset['metadata']['timestamp']
        version = dataset['metadata']['version']

        # Find benchmark for this method with standard parameters
        for benchmark in dataset['benchmarks']:
            if (benchmark['method'] == method and
                benchmark['parameters'].get('DataSize') == 1 and
                benchmark['parameters'].get('ModelType') == 'Small'):

                mean = benchmark['statistics']['mean']
                points.append({
                    'timestamp': timestamp,
                    'version': version,
                    'mean': mean
                })
                break

    if len(points) < 2:
        return False

    # Simple SVG generation
    width = 400
    height = 200
    margin = 40

    # Calculate scales
    min_mean = min(p['mean'] for p in points)
    max_mean = max(p['mean'] for p in points)
    mean_range = max_mean - min_mean if max_mean > min_mean else 1

    svg_content = f'''<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">
  <defs>
    <style>
      .chart-bg {{ fill: #f8f9fa; }}
      .chart-line {{ fill: none; stroke: #007bff; stroke-width: 2; }}
      .chart-point {{ fill: #007bff; }}
      .chart-text {{ font-family: Arial, sans-serif; font-size: 12px; fill: #333; }}
      .chart-title {{ font-family: Arial, sans-serif; font-size: 14px; font-weight: bold; fill: #333; }}
    </style>
  </defs>

  <!-- Background -->
  <rect class="chart-bg" width="{width}" height="{height}"/>

  <!-- Title -->
  <text class="chart-title" x="{width/2}" y="20" text-anchor="middle">{method} Performance Trend</text>

  <!-- Chart area -->
  <g transform="translate({margin}, {margin})">
'''

    chart_width = width - 2 * margin
    chart_height = height - 2 * margin - 20

    # Generate line path
    path_data = "M"
    for i, point in enumerate(points):
        x = (i / (len(points) - 1)) * chart_width
        y = chart_height - ((point['mean'] - min_mean) / mean_range) * chart_height

        if i == 0:
            path_data += f" {x} {y}"
        else:
            path_data += f" L {x} {y}"

    svg_content += f'    <path class="chart-line" d="{path_data}"/>\n'

    # Add points
    for i, point in enumerate(points):
        x = (i / (len(points) - 1)) * chart_width
        y = chart_height - ((point['mean'] - min_mean) / mean_range) * chart_height
        svg_content += f'    <circle class="chart-point" cx="{x}" cy="{y}" r="3"/>\n'

    # Add axes labels
    svg_content += f'    <text class="chart-text" x="0" y="{chart_height + 15}" text-anchor="start">{points[0]["version"]}</text>\n'
    svg_content += f'    <text class="chart-text" x="{chart_width}" y="{chart_height + 15}" text-anchor="end">{points[-1]["version"]}</text>\n'

    # Y-axis labels
    svg_content += f'    <text class="chart-text" x="-5" y="5" text-anchor="end">{max_mean:.0f}ns</text>\n'
    svg_content += f'    <text class="chart-text" x="-5" y="{chart_height + 5}" text-anchor="end">{min_mean:.0f}ns</text>\n'

    svg_content += '''  </g>
</svg>'''

    try:
        with open(output_path, 'w') as f:
            f.write(svg_content)
        return True
    except IOError:
        return False

def main():
    parser = argparse.ArgumentParser(description='Generate performance charts')
    parser.add_argument('--output-dir', default='.', help='Output directory for charts')
    args = parser.parse_args()

    print("Loading performance data...")
    data = load_performance_data()

    if len(data) < 2:
        print("Not enough data for trend analysis (need at least 2 data points)")
        return 0

    print(f"Found {len(data)} performance data files")

    # Generate charts for key methods
    key_methods = ['CacheHit', 'CacheMiss', 'NoCaching']

    generated_charts = 0
    for method in key_methods:
        output_path = f"{args.output_dir}/{method.lower()}-trend.svg"
        if generate_performance_trend_svg(data, method, output_path):
            print(f"Generated chart: {output_path}")
            generated_charts += 1
        else:
            print(f"Could not generate chart for {method}")

    if generated_charts == 0:
        print("No charts were generated")
        return 1

    print(f"Successfully generated {generated_charts} performance charts")
    return 0

if __name__ == '__main__':
    sys.exit(main())