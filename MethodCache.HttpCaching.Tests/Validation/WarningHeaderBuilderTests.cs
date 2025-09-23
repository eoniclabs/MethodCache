using MethodCache.HttpCaching.Validation;
using System.Net;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Validation;

public class WarningHeaderBuilderTests
{
    [Fact]
    public void AddWarning_ShouldCreateWarningWithCorrectCode()
    {
        // Arrange
        var builder = new WarningHeaderBuilder("test-host");

        // Act
        builder.AddWarning(WarningHeaderBuilder.WarningCode.ResponseIsStale, "Response is stale");
        var warnings = builder.GetWarnings().ToList();

        // Assert
        Assert.Single(warnings);
        Assert.Equal(110, warnings[0].Code);
        Assert.Equal("\"Response is stale\"", warnings[0].Text);
        Assert.Contains("test-host", warnings[0].Agent);
    }

    [Fact]
    public void AddStaleWarning_ShouldUseCorrectCode()
    {
        // Arrange
        var builder = new WarningHeaderBuilder();

        // Act
        builder.AddStaleWarning("Custom stale reason");
        var warnings = builder.GetWarnings().ToList();

        // Assert
        Assert.Single(warnings);
        Assert.Equal(110, warnings[0].Code);
        Assert.Equal("\"Custom stale reason\"", warnings[0].Text);
    }

    [Fact]
    public void AddRevalidationFailedWarning_ShouldUseCorrectCode()
    {
        // Arrange
        var builder = new WarningHeaderBuilder();

        // Act
        builder.AddRevalidationFailedWarning("Network error");
        var warnings = builder.GetWarnings().ToList();

        // Assert
        Assert.Single(warnings);
        Assert.Equal(111, warnings[0].Code);
        Assert.Equal("\"Network error\"", warnings[0].Text);
    }

    [Fact]
    public void AddHeuristicExpirationWarning_ShouldIncludeAge()
    {
        // Arrange
        var builder = new WarningHeaderBuilder();

        // Act
        builder.AddHeuristicExpirationWarning(TimeSpan.FromHours(2));
        var warnings = builder.GetWarnings().ToList();

        // Assert
        Assert.Single(warnings);
        Assert.Equal(113, warnings[0].Code);
        Assert.Contains("2 hours", warnings[0].Text);
    }

    [Fact]
    public void ApplyTo_ShouldAddWarningsToResponse()
    {
        // Arrange
        var builder = new WarningHeaderBuilder();
        builder.AddStaleWarning();
        builder.AddRevalidationFailedWarning();

        var response = new HttpResponseMessage(HttpStatusCode.OK);

        // Act
        builder.ApplyTo(response);

        // Assert
        Assert.Equal(2, response.Headers.Warning.Count);
        Assert.Contains(response.Headers.Warning, w => w.Code == 110);
        Assert.Contains(response.Headers.Warning, w => w.Code == 111);
    }

    [Fact]
    public void Remove1xxWarnings_ShouldRemoveOnly1xxCodes()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var builder = new WarningHeaderBuilder();

        builder.AddWarning(WarningHeaderBuilder.WarningCode.ResponseIsStale, "110 warning"); // 110
        builder.AddWarning(WarningHeaderBuilder.WarningCode.TransformationApplied, "214 warning"); // 214
        builder.AddWarning(WarningHeaderBuilder.WarningCode.MiscellaneousPersistentWarning, "299 warning"); // 299
        builder.ApplyTo(response);

        // Act
        WarningHeaderBuilder.Remove1xxWarnings(response);

        // Assert
        Assert.Equal(2, response.Headers.Warning.Count);
        Assert.DoesNotContain(response.Headers.Warning, w => w.Code == 110);
        Assert.Contains(response.Headers.Warning, w => w.Code == 214);
        Assert.Contains(response.Headers.Warning, w => w.Code == 299);
    }

    [Fact]
    public void HasWarnings_ShouldReturnCorrectValue()
    {
        // Arrange
        var responseWithWarnings = new HttpResponseMessage(HttpStatusCode.OK);
        var responseWithoutWarnings = new HttpResponseMessage(HttpStatusCode.OK);

        var builder = new WarningHeaderBuilder();
        builder.AddStaleWarning();
        builder.ApplyTo(responseWithWarnings);

        // Act & Assert
        Assert.True(WarningHeaderBuilder.HasWarnings(responseWithWarnings));
        Assert.False(WarningHeaderBuilder.HasWarnings(responseWithoutWarnings));
    }

    [Fact]
    public void HasWarningCode_ShouldDetectSpecificCode()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var builder = new WarningHeaderBuilder();
        builder.AddStaleWarning();
        builder.AddRevalidationFailedWarning();
        builder.ApplyTo(response);

        // Act & Assert
        Assert.True(WarningHeaderBuilder.HasWarningCode(response, WarningHeaderBuilder.WarningCode.ResponseIsStale));
        Assert.True(WarningHeaderBuilder.HasWarningCode(response, WarningHeaderBuilder.WarningCode.RevalidationFailed));
        Assert.False(WarningHeaderBuilder.HasWarningCode(response, WarningHeaderBuilder.WarningCode.DisconnectedOperation));
    }

    [Fact]
    public void Builder_ShouldSupportMethodChaining()
    {
        // Arrange & Act
        var warnings = new WarningHeaderBuilder()
            .AddStaleWarning()
            .AddRevalidationFailedWarning()
            .AddDisconnectedWarning()
            .GetWarnings()
            .ToList();

        // Assert
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Code == 110);
        Assert.Contains(warnings, w => w.Code == 111);
        Assert.Contains(warnings, w => w.Code == 112);
    }

    [Fact]
    public void AddWarning_WithDate_ShouldIncludeDate()
    {
        // Arrange
        var builder = new WarningHeaderBuilder();
        var testDate = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

        // Act
        builder.AddWarning(WarningHeaderBuilder.WarningCode.ResponseIsStale, "Test warning", testDate);
        var warning = builder.GetWarnings().First();

        // Assert
        Assert.Equal(testDate, warning.Date);
    }
}