using MethodCache.HttpCaching.Metrics;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Metrics;

public class HttpCacheMetricsTests
{
    [Fact]
    public void RecordHit_ShouldIncreaseHitCount()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordHit(100);
        metrics.RecordHit(200);

        // Assert
        Assert.Equal(2, metrics.HitCount);
    }

    [Fact]
    public void RecordMiss_ShouldIncreaseMissCount()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordMiss(100);
        metrics.RecordMiss(200);
        metrics.RecordMiss(300);

        // Assert
        Assert.Equal(3, metrics.MissCount);
    }

    [Fact]
    public void HitRate_ShouldCalculateCorrectPercentage()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordHit(100);
        metrics.RecordHit(200);
        metrics.RecordHit(300);
        metrics.RecordMiss(100);
        metrics.RecordMiss(200);

        // Assert
        Assert.Equal(0.6, metrics.HitRate, 2); // 3 hits / 5 total = 60%
    }

    [Fact]
    public void HitRate_WithNoRequests_ShouldReturnZero()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act & Assert
        Assert.Equal(0.0, metrics.HitRate);
    }

    [Fact]
    public void AverageResponseTimeMs_ShouldCalculateCorrectly()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordHit(100);
        metrics.RecordMiss(200);
        metrics.RecordHit(300);

        // Assert
        Assert.Equal(200.0, metrics.AverageResponseTimeMs, 1);
    }

    [Fact]
    public void RecordStaleServed_ShouldIncreaseStaleCount()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordStaleServed(100);
        metrics.RecordStaleServed(200);

        // Assert
        Assert.Equal(2, metrics.StaleServedCount);
    }

    [Fact]
    public void RecordValidation_ShouldIncreaseValidationCount()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordValidation(100);

        // Assert
        Assert.Equal(1, metrics.ValidationRequestCount);
    }

    [Fact]
    public void RecordBypass_ShouldIncreaseBypassCount()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordBypass(100);
        metrics.RecordBypass(200);

        // Assert
        Assert.Equal(2, metrics.BypassCount);
    }

    [Fact]
    public void RecordError_ShouldIncreaseErrorCount()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordError();
        metrics.RecordError();

        // Assert
        Assert.Equal(2, metrics.ErrorCount);
    }

    [Fact]
    public void TotalRequests_ShouldSumHitsMissesAndBypasses()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordHit(100);
        metrics.RecordMiss(200);
        metrics.RecordBypass(300);

        // Assert
        Assert.Equal(3, metrics.TotalRequests);
    }

    [Fact]
    public void GetPercentileResponseTime_ShouldCalculateCorrectly()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();
        var times = new[] { 100, 200, 300, 400, 500 };

        // Act
        foreach (var time in times)
        {
            metrics.RecordHit(time);
        }

        // Assert
        Assert.Equal(100, metrics.GetPercentileResponseTime(0));
        Assert.Equal(300, metrics.GetPercentileResponseTime(50));
        Assert.Equal(500, metrics.GetPercentileResponseTime(100));
    }

    [Fact]
    public void GetPercentileResponseTime_WithNoData_ShouldReturnZero()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act & Assert
        Assert.Equal(0, metrics.GetPercentileResponseTime(50));
    }

    [Fact]
    public void RecordStatusCode_ShouldTrackStatusCodes()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordStatusCode(200);
        metrics.RecordStatusCode(200);
        metrics.RecordStatusCode(404);
        metrics.RecordStatusCode(500);

        // Assert
        Assert.Equal(2, metrics.StatusCodeCounts["200"]);
        Assert.Equal(1, metrics.StatusCodeCounts["404"]);
        Assert.Equal(1, metrics.StatusCodeCounts["500"]);
        Assert.False(metrics.StatusCodeCounts.ContainsKey("301"));
    }

    [Fact]
    public void RecordMethod_ShouldTrackHttpMethods()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordMethod("GET");
        metrics.RecordMethod("GET");
        metrics.RecordMethod("POST");

        // Assert
        Assert.Equal(2, metrics.MethodCounts["GET"]);
        Assert.Equal(1, metrics.MethodCounts["POST"]);
    }

    [Fact]
    public void AverageResponseTime_WithNoData_ShouldReturnZero()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act & Assert
        Assert.Equal(0, metrics.AverageResponseTimeMs);
    }

    [Fact]
    public void Reset_ShouldClearAllMetrics()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();
        metrics.RecordHit(100);
        metrics.RecordMiss(200);
        metrics.RecordStatusCode(200);
        metrics.RecordMethod("GET");
        metrics.RecordError();

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, metrics.HitCount);
        Assert.Equal(0, metrics.MissCount);
        Assert.Equal(0, metrics.ErrorCount);
        Assert.Equal(0, metrics.AverageResponseTimeMs);
        Assert.Empty(metrics.StatusCodeCounts);
        Assert.Empty(metrics.MethodCounts);
    }

    [Fact]
    public async Task Metrics_ShouldBeThreadSafe()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();
        var tasks = new List<Task>();

        // Act - Simulate concurrent access
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    metrics.RecordHit(j);
                    metrics.RecordMiss(j);
                    metrics.RecordStatusCode(200);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1000, metrics.HitCount);
        Assert.Equal(1000, metrics.MissCount);
        Assert.Equal(1000, metrics.StatusCodeCounts["200"]);
        Assert.True(metrics.AverageResponseTimeMs >= 0);
    }

    [Fact]
    public void GetPercentileResponseTime_WithSingleValue_ShouldReturnThatValue()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act
        metrics.RecordHit(150);

        // Assert
        Assert.Equal(150, metrics.GetPercentileResponseTime(50));
        Assert.Equal(150, metrics.GetPercentileResponseTime(95));
    }

    [Fact]
    public void GetSnapshot_ShouldCaptureCurrentState()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();
        metrics.RecordHit(100);
        metrics.RecordMiss(200);
        metrics.RecordStatusCode(200);
        metrics.RecordMethod("GET");

        // Act
        var snapshot = metrics.GetSnapshot();

        // Assert
        Assert.Equal(1, snapshot.HitCount);
        Assert.Equal(1, snapshot.MissCount);
        Assert.Equal(0.5, snapshot.HitRate);
        Assert.Equal(150.0, snapshot.AverageResponseTimeMs);
        Assert.Contains("200", snapshot.StatusCodeCounts.Keys);
        Assert.Contains("GET", snapshot.MethodCounts.Keys);
        Assert.True(snapshot.Timestamp <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void GetPercentileResponseTime_WithInvalidPercentile_ShouldThrow()
    {
        // Arrange
        var metrics = new HttpCacheMetrics();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => metrics.GetPercentileResponseTime(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => metrics.GetPercentileResponseTime(101));
    }
}