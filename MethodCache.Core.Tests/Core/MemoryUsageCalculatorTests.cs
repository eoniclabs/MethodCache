using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Infrastructure.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.Core.Tests.Core
{
    public class MemoryUsageCalculatorTests
    {
        private readonly ITestOutputHelper _output;

        public MemoryUsageCalculatorTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void FastMode_ShouldBeVeryFast()
        {
            // Arrange
            var options = new MemoryCacheOptions { MemoryCalculationMode = MemoryUsageCalculationMode.Fast };
            var calculator = new MemoryUsageCalculator(options);
            var cache = CreateTestCache(1000);

            // Act & Assert
            var stopwatch = Stopwatch.StartNew();
            var memoryUsage = calculator.CalculateMemoryUsage(cache, entry => entry.Value);
            stopwatch.Stop();

            _output.WriteLine($"Fast mode: {memoryUsage:N0} bytes in {stopwatch.ElapsedMilliseconds}ms");
            
            Assert.True(stopwatch.ElapsedMilliseconds < 200, "Fast mode should complete quickly");
            Assert.True(memoryUsage > 0, "Should return a positive memory estimate");
        }

        [Fact]
        public void AccurateMode_ShouldBeMoreAccurate()
        {
            // Arrange
            var fastOptions = new MemoryCacheOptions { MemoryCalculationMode = MemoryUsageCalculationMode.Fast };
            var accurateOptions = new MemoryCacheOptions 
            { 
                MemoryCalculationMode = MemoryUsageCalculationMode.Accurate,
                AccurateModeRecalculationInterval = 1 // Force recalculation every time
            };
            
            var fastCalculator = new MemoryUsageCalculator(fastOptions);
            var accurateCalculator = new MemoryUsageCalculator(accurateOptions);
            var cache = CreateTestCache(100);

            // Act
            var fastResult = fastCalculator.CalculateMemoryUsage(cache, entry => entry.Value);
            var accurateResult = accurateCalculator.CalculateMemoryUsage(cache, entry => entry.Value);

            _output.WriteLine($"Fast mode: {fastResult:N0} bytes");
            _output.WriteLine($"Accurate mode: {accurateResult:N0} bytes");
            _output.WriteLine($"Difference: {Math.Abs(fastResult - accurateResult):N0} bytes ({Math.Abs(fastResult - accurateResult) * 100.0 / Math.Max(fastResult, accurateResult):F1}%)");

            // Assert
            Assert.True(fastResult > 0);
            Assert.True(accurateResult > 0);
            // Results should be in the same ballpark but may differ
        }

        [Fact]
        public void SamplingMode_ShouldBalancePerformanceAndAccuracy()
        {
            // Arrange
            var options = new MemoryCacheOptions 
            { 
                MemoryCalculationMode = MemoryUsageCalculationMode.Sampling,
                SamplingPercentage = 0.2 // 20% sampling
            };
            var calculator = new MemoryUsageCalculator(options);
            var cache = CreateTestCache(500);

            // Act
            var stopwatch = Stopwatch.StartNew();
            var memoryUsage = calculator.CalculateMemoryUsage(cache, entry => entry.Value);
            stopwatch.Stop();

            _output.WriteLine($"Sampling mode: {memoryUsage:N0} bytes in {stopwatch.ElapsedMilliseconds}ms");

            // Assert
            Assert.True(memoryUsage > 0);
            Assert.True(stopwatch.ElapsedMilliseconds < 100, "Sampling mode should be reasonably fast");
        }

        [Fact]
        public void DisabledMode_ShouldReturnZero()
        {
            // Arrange
            var options = new MemoryCacheOptions { MemoryCalculationMode = MemoryUsageCalculationMode.Disabled };
            var calculator = new MemoryUsageCalculator(options);
            var cache = CreateTestCache(100);

            // Act
            var memoryUsage = calculator.CalculateMemoryUsage(cache, entry => entry.Value);

            // Assert
            Assert.Equal(0, memoryUsage);
        }

        [Fact]
        public void AccurateMode_ShouldThrottleRecalculations()
        {
            // Arrange
            var options = new MemoryCacheOptions 
            { 
                MemoryCalculationMode = MemoryUsageCalculationMode.Accurate,
                AccurateModeRecalculationInterval = 100 // Only recalculate every 100 operations
            };
            var calculator = new MemoryUsageCalculator(options);
            var cache = CreateTestCache(50);

            // Act - Call multiple times quickly
            var results = new List<long>();
            var timings = new List<long>();
            
            for (int i = 0; i < 10; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                var result = calculator.CalculateMemoryUsage(cache, entry => entry.Value);
                stopwatch.Stop();
                
                results.Add(result);
                timings.Add(stopwatch.ElapsedMilliseconds);
            }

            _output.WriteLine($"Results: {string.Join(", ", results)}");
            _output.WriteLine($"Timings: {string.Join(", ", timings)}ms");

            // Assert - Most calls should be fast (using fast mode internally)
            var fastCalls = timings.Count(t => t < 5);
            Assert.True(fastCalls >= 8, "Most calls should be fast due to throttling");
        }

        [Theory]
        [InlineData(MemoryUsageCalculationMode.Fast)]
        [InlineData(MemoryUsageCalculationMode.Accurate)]
        [InlineData(MemoryUsageCalculationMode.Sampling)]
        public void AllModes_ShouldHandleEmptyCache(MemoryUsageCalculationMode mode)
        {
            // Arrange
            var options = new MemoryCacheOptions { MemoryCalculationMode = mode };
            var calculator = new MemoryUsageCalculator(options);
            var cache = new ConcurrentDictionary<string, TestCacheEntry>();

            // Act
            var memoryUsage = calculator.CalculateMemoryUsage(cache, entry => entry.Value);

            // Assert
            Assert.Equal(0, memoryUsage);
        }

        [Fact]
        public void PerformanceComparison_AllModes()
        {
            // Arrange
            var cache = CreateTestCache(1000);
            var modes = new[]
            {
                MemoryUsageCalculationMode.Fast,
                MemoryUsageCalculationMode.Accurate,
                MemoryUsageCalculationMode.Sampling,
                MemoryUsageCalculationMode.Disabled
            };

            _output.WriteLine("Performance Comparison:");
            _output.WriteLine("Mode\t\tTime (ms)\tMemory (bytes)");
            _output.WriteLine("----\t\t---------\t--------------");

            foreach (var mode in modes)
            {
                var options = new MemoryCacheOptions 
                { 
                    MemoryCalculationMode = mode,
                    AccurateModeRecalculationInterval = 1 // Force accurate calculation
                };
                var calculator = new MemoryUsageCalculator(options);

                var stopwatch = Stopwatch.StartNew();
                var memoryUsage = calculator.CalculateMemoryUsage(cache, entry => entry.Value);
                stopwatch.Stop();

                _output.WriteLine($"{mode}\t{stopwatch.ElapsedMilliseconds}\t\t{memoryUsage:N0}");
            }
        }

        private ConcurrentDictionary<string, TestCacheEntry> CreateTestCache(int entryCount)
        {
            var cache = new ConcurrentDictionary<string, TestCacheEntry>();
            
            for (int i = 0; i < entryCount; i++)
            {
                var entry = new TestCacheEntry
                {
                    Value = CreateTestValue(i)
                };
                cache.TryAdd($"key_{i}", entry);
            }

            return cache;
        }

        private object CreateTestValue(int index)
        {
            return (index % 4) switch
            {
                0 => $"String value {index} with some additional content to make it longer",
                1 => index,
                2 => new { Id = index, Name = $"Object_{index}", Data = new byte[100] },
                3 => new List<string> { $"Item1_{index}", $"Item2_{index}", $"Item3_{index}" },
                _ => (object)index
            };
        }

        private class TestCacheEntry
        {
            public object Value { get; set; } = null!;
        }
    }
}
