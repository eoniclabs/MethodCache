using System.Linq;
using FluentAssertions;
using MethodCache.OpenTelemetry.Security;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests.Security;

public class PIIDetectorTests
{
    private readonly RegexPIIDetector _detector;

    public PIIDetectorTests()
    {
        _detector = new RegexPIIDetector();
    }

    [Theory]
    [InlineData("john.doe@example.com", true)]
    [InlineData("test@domain.co.uk", true)]
    [InlineData("invalid-email", false)]
    [InlineData("@example.com", false)]
    public void DetectPII_EmailAddresses_ReturnsCorrectResults(string text, bool shouldDetect)
    {
        // Act
        var detections = _detector.DetectPII(text).ToList();

        // Assert
        if (shouldDetect)
        {
            detections.Should().ContainSingle(d => d.Type == PIIType.EmailAddress);
        }
        else
        {
            detections.Should().NotContain(d => d.Type == PIIType.EmailAddress);
        }
    }

    [Theory]
    [InlineData("555-123-4567", true)]
    [InlineData("(555) 123-4567", true)]
    [InlineData("+1-555-123-4567", true)]
    [InlineData("123-45", false)]
    [InlineData("not-a-phone", false)]
    public void DetectPII_PhoneNumbers_ReturnsCorrectResults(string text, bool shouldDetect)
    {
        // Act
        var detections = _detector.DetectPII(text).ToList();

        // Assert
        if (shouldDetect)
        {
            detections.Should().ContainSingle(d => d.Type == PIIType.PhoneNumber);
        }
        else
        {
            detections.Should().NotContain(d => d.Type == PIIType.PhoneNumber);
        }
    }

    [Theory]
    [InlineData("123-45-6789", true)]
    [InlineData("123 45 6789", true)]
    [InlineData("123456789", true)]
    [InlineData("123-45-678", false)]
    public void DetectPII_SocialSecurityNumbers_ReturnsCorrectResults(string text, bool shouldDetect)
    {
        // Act
        var detections = _detector.DetectPII(text).ToList();

        // Assert
        if (shouldDetect)
        {
            detections.Should().ContainSingle(d => d.Type == PIIType.SocialSecurityNumber);
        }
        else
        {
            detections.Should().NotContain(d => d.Type == PIIType.SocialSecurityNumber);
        }
    }

    [Theory]
    [InlineData("4532-1234-5678-9012", true)]  // Visa
    [InlineData("5555-5555-5555-4444", true)]  // MasterCard
    [InlineData("3782-822463-10005", true)]    // American Express
    [InlineData("1234-5678-9012", false)]      // Too short
    public void DetectPII_CreditCardNumbers_ReturnsCorrectResults(string text, bool shouldDetect)
    {
        // Act
        var detections = _detector.DetectPII(text).ToList();

        // Assert
        if (shouldDetect)
        {
            detections.Should().ContainSingle(d => d.Type == PIIType.CreditCardNumber);
        }
        else
        {
            detections.Should().NotContain(d => d.Type == PIIType.CreditCardNumber);
        }
    }

    [Fact]
    public void DetectPII_MultipleTypes_ReturnsAllDetections()
    {
        // Arrange
        var text = "Contact John Doe at john.doe@example.com or call 555-123-4567";

        // Act
        var detections = _detector.DetectPII(text).ToList();

        // Assert
        detections.Should().HaveCountGreaterOrEqualTo(2);
        detections.Should().Contain(d => d.Type == PIIType.EmailAddress);
        detections.Should().Contain(d => d.Type == PIIType.PhoneNumber);
    }

    [Fact]
    public void ContainsPII_WithPII_ReturnsTrue()
    {
        // Arrange
        var text = "Email me at test@example.com";

        // Act
        var containsPII = _detector.ContainsPII(text);

        // Assert
        containsPII.Should().BeTrue();
    }

    [Fact]
    public void ContainsPII_WithoutPII_ReturnsFalse()
    {
        // Arrange
        var text = "This is just normal text without any sensitive information";

        // Act
        var containsPII = _detector.ContainsPII(text);

        // Assert
        containsPII.Should().BeFalse();
    }

    [Fact]
    public void DetectPII_WithFieldName_IncludesFieldNameInResult()
    {
        // Arrange
        var text = "john.doe@example.com";
        var fieldName = "userEmail";

        // Act
        var detections = _detector.DetectPII(text, fieldName).ToList();

        // Assert
        detections.Should().ContainSingle()
            .Which.FieldName.Should().Be(fieldName);
    }
}

public class PIIRedactorTests
{
    private readonly RegexPIIDetector _detector;
    private readonly PIIRedactor _redactor;

    public PIIRedactorTests()
    {
        _detector = new RegexPIIDetector();
        _redactor = new PIIRedactor(_detector);
    }

    [Theory]
    [InlineData("Contact me at john.doe@example.com", PIIRedactionStrategy.Mask, "********")]
    [InlineData("Call 555-123-4567", PIIRedactionStrategy.PartialMask, "*")]
    [InlineData("Email: test@domain.com", PIIRedactionStrategy.Placeholder, "<REDACTED>")]
    [InlineData("Send to user@example.org", PIIRedactionStrategy.Remove, "")]
    public void RedactPII_WithDifferentStrategies_ReturnsExpectedResults(
        string input,
        PIIRedactionStrategy strategy,
        string expectedPattern)
    {
        // Act
        var result = _redactor.RedactDetectedPII(input, _detector.DetectPII(input), strategy);

        // Assert
        if (strategy == PIIRedactionStrategy.Hash)
        {
            result.Should().Contain("<hash:");
        }
        else if (strategy == PIIRedactionStrategy.Remove && expectedPattern == "")
        {
            // For remove strategy, check that PII is not present in original form
            var originalPII = _detector.DetectPII(input).FirstOrDefault();
            if (originalPII != null)
            {
                result.Should().NotContain(originalPII.Value);
            }
        }
        else
        {
            // For other strategies, check that expected pattern is present
            result.Should().Contain(expectedPattern);
        }
    }

    [Fact]
    public void RedactPII_WithNoPII_ReturnsOriginalText()
    {
        // Arrange
        var text = "This text contains no sensitive information";

        // Act
        var result = _redactor.RedactPII(text);

        // Assert
        result.Should().Be(text);
    }

    [Fact]
    public void RedactPII_WithMultiplePIITypes_RedactsAll()
    {
        // Arrange
        var text = "Contact John at john.doe@example.com or 555-123-4567";

        // Act
        var result = _redactor.RedactPII(text);

        // Assert
        result.Should().NotContain("john.doe@example.com");
        result.Should().NotContain("555-123-4567");
    }
}