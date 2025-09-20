#!/usr/bin/env python3
"""
Performance Chart Generator for MethodCache

Generates SVG charts from performance data for embedding in README.md
"""

import json
import glob
import argparse
from datetime import datetime
from typing import List, Dict, Any
import xml.etree.ElementTree as ET


def load_performance_data(limit: int = 50) -> List[Dict]:
    """Load performance data files, sorted by timestamp"""
    data_files = sorted(glob.glob('benchmark-*.json'), reverse=True)

    if limit > 0:
        data_files = data_files[:limit]

    data = []
    for file_path in data_files:
        try:
            with open(file_path, 'r') as f:
                data.append(json.load(f))
        except Exception as e:
            print(f"Error loading {file_path}: {e}")

    return list(reversed(data))  # Oldest first for charting


def create_svg_chart(data: List[Dict], method: str, title: str, width: int = 800, height: int = 400) -> str:
    """Create an SVG line chart for a specific benchmark method"""

    # Extract data points for the method
    points = []
    for entry in data:
        for benchmark in entry['benchmarks']:
            if (benchmark['method'] == method and
                benchmark['parameters'].get('DataSize') == 1 and
                benchmark['parameters'].get('ModelType') == 'Small'):

                timestamp = datetime.fromisoformat(entry['metadata']['timestamp'].replace('Z', '+00:00'))
                mean = benchmark['statistics']['mean']
                version = entry['metadata']['version']

                if mean > 0:  # Only include valid measurements
                    points.append({
                        'timestamp': timestamp,
                        'mean': mean,
                        'version': version,
                        'date': timestamp.strftime('%Y-%m-%d')
                    })
                break

    if len(points) < 2:
        return f"<!-- Not enough data points for {method} chart -->"

    # Calculate chart dimensions and scales
    margin = 60
    chart_width = width - 2 * margin
    chart_height = height - 2 * margin

    min_time = min(p['timestamp'] for p in points)
    max_time = max(p['timestamp'] for p in points)
    time_range = (max_time - min_time).total_seconds()

    min_mean = min(p['mean'] for p in points)
    max_mean = max(p['mean'] for p in points)
    mean_range = max_mean - min_mean if max_mean != min_mean else max_mean * 0.1

    # Create SVG
    svg = ET.Element('svg', {
        'width': str(width),
        'height': str(height),
        'viewBox': f'0 0 {width} {height}',
        'xmlns': 'http://www.w3.org/2000/svg'
    })

    # Add styles
    style = ET.SubElement(svg, 'style')
    style.text = """
        .chart-line { fill: none; stroke: #2563eb; stroke-width: 2; }
        .chart-point { fill: #2563eb; }
        .chart-grid { stroke: #e5e7eb; stroke-width: 1; }
        .chart-text { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; font-size: 12px; fill: #374151; }
        .chart-title { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; font-size: 16px; font-weight: bold; fill: #111827; }
        .chart-axis { stroke: #9ca3af; stroke-width: 1; }
    """

    # Add title
    title_elem = ET.SubElement(svg, 'text', {
        'x': str(width // 2),
        'y': '25',
        'text-anchor': 'middle',
        'class': 'chart-title'
    })
    title_elem.text = title

    # Add grid lines and axes
    # Y-axis
    ET.SubElement(svg, 'line', {
        'x1': str(margin),
        'y1': str(margin),
        'x2': str(margin),
        'y2': str(height - margin),
        'class': 'chart-axis'
    })

    # X-axis
    ET.SubElement(svg, 'line', {
        'x1': str(margin),
        'y1': str(height - margin),
        'x2': str(width - margin),
        'y2': str(height - margin),
        'class': 'chart-axis'
    })

    # Generate grid lines and labels
    for i in range(5):
        y = margin + (chart_height * i / 4)
        value = max_mean - (mean_range * i / 4)

        # Grid line
        ET.SubElement(svg, 'line', {
            'x1': str(margin),
            'y1': str(y),
            'x2': str(width - margin),
            'y2': str(y),
            'class': 'chart-grid'
        })

        # Y-axis label
        label = ET.SubElement(svg, 'text', {
            'x': str(margin - 10),
            'y': str(y + 4),
            'text-anchor': 'end',
            'class': 'chart-text'
        })
        label.text = f'{value:.1f}'

    # Create line path
    path_data = []
    for i, point in enumerate(points):
        time_progress = (point['timestamp'] - min_time).total_seconds() / time_range if time_range > 0 else 0
        mean_progress = (max_mean - point['mean']) / mean_range if mean_range > 0 else 0.5

        x = margin + chart_width * time_progress
        y = margin + chart_height * mean_progress

        if i == 0:
            path_data.append(f'M {x:.1f} {y:.1f}')
        else:
            path_data.append(f'L {x:.1f} {y:.1f}')

        # Add point
        ET.SubElement(svg, 'circle', {
            'cx': str(x),
            'cy': str(y),
            'r': '3',
            'class': 'chart-point'
        })

        # Add point label for recent versions
        if i >= len(points) - 5:  # Last 5 points
            label = ET.SubElement(svg, 'text', {
                'x': str(x),
                'y': str(y - 10),
                'text-anchor': 'middle',
                'class': 'chart-text'
            })
            label.text = point['version'][:8]

    # Add line path
    if path_data:
        ET.SubElement(svg, 'path', {
            'd': ' '.join(path_data),
            'class': 'chart-line'
        })

    # Add X-axis labels (dates)
    for i in range(min(6, len(points))):
        if len(points) > 1:
            point_index = i * (len(points) - 1) // 5
            point = points[point_index]

            time_progress = (point['timestamp'] - min_time).total_seconds() / time_range if time_range > 0 else 0
            x = margin + chart_width * time_progress

            label = ET.SubElement(svg, 'text', {
                'x': str(x),
                'y': str(height - margin + 20),
                'text-anchor': 'middle',
                'class': 'chart-text'
            })
            label.text = point['date']

    # Y-axis title
    y_title = ET.SubElement(svg, 'text', {
        'x': '15',
        'y': str(height // 2),
        'text-anchor': 'middle',
        'transform': f'rotate(-90, 15, {height // 2})',
        'class': 'chart-text'
    })
    y_title.text = 'Mean Time (ns)'

    return ET.tostring(svg, encoding='unicode')


def generate_performance_summary(data: List[Dict]) -> str:
    """Generate a performance summary table"""
    if not data:
        return "No performance data available."

    latest = data[-1]

    # Group benchmarks by method
    methods = {}
    for benchmark in latest['benchmarks']:
        method = benchmark['method']
        if method not in methods:
            methods[method] = []
        methods[method].append(benchmark)

    summary = []
    summary.append("| Method | Small (1 item) | Medium (1 item) | Large (1 item) |")
    summary.append("|--------|----------------|-----------------|----------------|")

    for method in sorted(methods.keys()):
        benchmarks = methods[method]
        row = [method]

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
                    row.append(f"{mean_ns:.1f} ns")
                elif mean_ns < 1000000:
                    row.append(f"{mean_ns/1000:.1f} μs")
                else:
                    row.append(f"{mean_ns/1000000:.1f} ms")
            else:
                row.append("N/A")

        summary.append("| " + " | ".join(row) + " |")

    return "\n".join(summary)


def main():
    parser = argparse.ArgumentParser(description='Generate performance charts for MethodCache')
    parser.add_argument('--output-dir', default='.', help='Output directory for charts')
    parser.add_argument('--limit', type=int, default=50, help='Maximum number of data files to process')
    args = parser.parse_args()

    print("Loading performance data...")
    data = load_performance_data(args.limit)

    if not data:
        print("No performance data found!")
        return

    print(f"Loaded {len(data)} performance data entries")

    # Generate charts for key methods
    methods = [
        ('CacheHit', 'Cache Hit Performance Over Time'),
        ('CacheMiss', 'Cache Miss Performance Over Time'),
        ('NoCaching', 'No Caching Performance Over Time'),
    ]

    charts = {}
    for method, title in methods:
        print(f"Generating chart for {method}...")
        chart_svg = create_svg_chart(data, method, title)
        charts[method] = chart_svg

        # Save individual chart
        with open(f'{args.output_dir}/chart-{method.lower()}.svg', 'w') as f:
            f.write(chart_svg)

    # Generate performance summary
    print("Generating performance summary...")
    summary = generate_performance_summary(data)

    # Create combined report
    latest_version = data[-1]['metadata']['version'] if data else 'unknown'
    latest_date = data[-1]['metadata']['timestamp'][:10] if data else 'unknown'

    report = f"""# 📊 MethodCache Performance Dashboard

**Latest Version:** {latest_version}
**Last Updated:** {latest_date}
**Total Benchmark Runs:** {len(data)}

## 🚀 Current Performance Summary

{summary}

## 📈 Performance Trends

### Cache Hit Performance
{charts.get('CacheHit', '<!-- Chart not available -->')}

### Cache Miss Performance
{charts.get('CacheMiss', '<!-- Chart not available -->')}

### Baseline (No Caching) Performance
{charts.get('NoCaching', '<!-- Chart not available -->')}

---
*Charts automatically generated from benchmark data. See [performance data](.performance-data/) for raw results.*
"""

    with open(f'{args.output_dir}/../PERFORMANCE.md', 'w') as f:
        f.write(report)

    print(f"Performance dashboard generated: PERFORMANCE.md")
    print(f"Individual charts saved in: {args.output_dir}/")


# Helper functions for create_svg_chart refactoring
def _create_svg_header(width: int, height: int) -> str:
    """Create SVG header with styles"""
    return f'''<svg width="{width}" height="{height}" xmlns="http://www.w3.org/2000/svg">
    <defs>
        <style>
            .chart-grid {{ stroke: #e0e0e0; stroke-width: 1; }}
            .chart-axis {{ stroke: #333; stroke-width: 2; }}
            .chart-line {{ fill: none; stroke: #007acc; stroke-width: 3; }}
            .chart-point {{ fill: #007acc; r: 4; }}
            .chart-text {{ font-family: Arial, sans-serif; font-size: 12px; fill: #333; }}
            .chart-title {{ font-family: Arial, sans-serif; font-size: 16px; font-weight: bold; fill: #333; }}
        </style>
    </defs>'''

def _create_chart_grid(width: int, height: int, margin: int, points, min_mean: float, max_mean: float, min_time, max_time) -> list:
    """Create chart grid lines"""
    elements = []
    chart_width = width - 2 * margin
    chart_height = height - 2 * margin

    # Horizontal grid lines (for mean values)
    for i in range(5):
        y = margin + i * (chart_height / 4)
        elements.append(f'<line x1="{margin}" y1="{y}" x2="{width - margin}" y2="{y}" class="chart-grid" />')

    # Vertical grid lines (for time)
    for i in range(5):
        x = margin + i * (chart_width / 4)
        elements.append(f'<line x1="{x}" y1="{margin}" x2="{x}" y2="{height - margin}" class="chart-grid" />')

    return elements

def _create_chart_axes(width: int, height: int, margin: int) -> list:
    """Create chart axes"""
    return [
        f'<line x1="{margin}" y1="{margin}" x2="{margin}" y2="{height - margin}" class="chart-axis" />',
        f'<line x1="{margin}" y1="{height - margin}" x2="{width - margin}" y2="{height - margin}" class="chart-axis" />'
    ]

def _plot_data_points(points, width: int, height: int, margin: int, min_mean: float, max_mean: float, min_time, max_time) -> list:
    """Plot the actual data points and line"""
    elements = []
    chart_width = width - 2 * margin
    chart_height = height - 2 * margin
    time_range = (max_time - min_time).total_seconds()
    mean_range = max_mean - min_mean if max_mean != min_mean else max_mean * 0.1

    # Calculate coordinates
    coords = []
    for point in points:
        time_offset = (point['timestamp'] - min_time).total_seconds()
        x = margin + (time_offset / time_range) * chart_width
        y = height - margin - ((point['mean'] - min_mean) / mean_range) * chart_height
        coords.append((x, y))

    # Create line path
    if len(coords) > 1:
        path_data = f"M {coords[0][0]} {coords[0][1]}"
        for x, y in coords[1:]:
            path_data += f" L {x} {y}"
        elements.append(f'<path d="{path_data}" class="chart-line" />')

    # Add data points
    for x, y in coords:
        elements.append(f'<circle cx="{x}" cy="{y}" class="chart-point" />')

    return elements

def _create_chart_labels(title: str, width: int, height: int, margin: int, points, min_mean: float, max_mean: float) -> list:
    """Create chart title and axis labels"""
    elements = []

    # Title
    elements.append(f'<text x="{width // 2}" y="25" text-anchor="middle" class="chart-title">{title}</text>')

    # Y-axis labels
    mean_range = max_mean - min_mean if max_mean != min_mean else max_mean * 0.1
    for i in range(5):
        y = margin + i * ((height - 2 * margin) / 4)
        value = max_mean - (i * mean_range / 4)
        if value >= 1000:
            label = f"{value/1000:.1f}μs"
        else:
            label = f"{value:.0f}ns"
        elements.append(f'<text x="{margin - 10}" y="{y + 4}" text-anchor="end" class="chart-text">{label}</text>')

    # X-axis labels (simplified)
    if points:
        elements.append(f'<text x="{margin}" y="{height - 10}" text-anchor="start" class="chart-text">{points[0]["date"]}</text>')
        elements.append(f'<text x="{width - margin}" y="{height - 10}" text-anchor="end" class="chart-text">{points[-1]["date"]}</text>')

    # Axis titles
    elements.append(f'<text x="{width // 2}" y="{height - 5}" text-anchor="middle" class="chart-text">Date</text>')
    elements.append(f'<text x="15" y="{height // 2}" text-anchor="middle" transform="rotate(-90, 15, {height // 2})" class="chart-text">Mean Time (ns)</text>')

    return elements

# Refactored create_svg_chart function
def create_svg_chart_refactored(data: List[Dict], method: str, title: str, width: int = 800, height: int = 400) -> str:
    """Create an SVG line chart for a specific benchmark method - refactored version"""

    # Extract data points for the method
    points = []
    for entry in data:
        for benchmark in entry['benchmarks']:
            if (benchmark['method'] == method and
                benchmark['parameters'].get('DataSize') == 1 and
                benchmark['parameters'].get('ModelType') == 'Small'):

                timestamp = datetime.fromisoformat(entry['metadata']['timestamp'].replace('Z', '+00:00'))
                mean = benchmark['statistics']['mean']
                version = entry['metadata']['version']

                if mean > 0:  # Only include valid measurements
                    points.append({
                        'timestamp': timestamp,
                        'mean': mean,
                        'version': version,
                        'date': timestamp.strftime('%Y-%m-%d')
                    })
                break

    if len(points) < 2:
        return f"<!-- Not enough data points for {method} chart -->"

    # Calculate chart dimensions and scales
    margin = 60
    min_time = min(p['timestamp'] for p in points)
    max_time = max(p['timestamp'] for p in points)
    min_mean = min(p['mean'] for p in points)
    max_mean = max(p['mean'] for p in points)

    # Build SVG
    svg_parts = []
    svg_parts.append(_create_svg_header(width, height))
    svg_parts.extend(_create_chart_grid(width, height, margin, points, min_mean, max_mean, min_time, max_time))
    svg_parts.extend(_create_chart_axes(width, height, margin))
    svg_parts.extend(_plot_data_points(points, width, height, margin, min_mean, max_mean, min_time, max_time))
    svg_parts.extend(_create_chart_labels(title, width, height, margin, points, min_mean, max_mean))
    svg_parts.append('</svg>')

    return '\n'.join(svg_parts)


if __name__ == '__main__':
    main()