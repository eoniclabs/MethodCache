using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MethodCache.OpenTelemetry.Security;

/// <summary>
/// Strategies for handling detected PII
/// </summary>
public enum PIIRedactionStrategy
{
    /// <summary>
    /// Remove the entire field/value containing PII
    /// </summary>
    Remove,

    /// <summary>
    /// Replace with asterisks (*****)
    /// </summary>
    Mask,

    /// <summary>
    /// Replace with a hash of the original value
    /// </summary>
    Hash,

    /// <summary>
    /// Keep only the first and last characters, mask the middle
    /// </summary>
    PartialMask,

    /// <summary>
    /// Replace with a generic placeholder
    /// </summary>
    Placeholder
}

/// <summary>
/// Types of PII that can be detected
/// </summary>
[Flags]
public enum PIIType
{
    None = 0,
    EmailAddress = 1,
    PhoneNumber = 2,
    SocialSecurityNumber = 4,
    CreditCardNumber = 8,
    IPAddress = 16,
    PersonName = 32,
    DateOfBirth = 64,
    Address = 128,
    Custom = 256,
    All = EmailAddress | PhoneNumber | SocialSecurityNumber | CreditCardNumber |
          IPAddress | PersonName | DateOfBirth | Address | Custom
}

/// <summary>
/// Represents detected PII in a string
/// </summary>
public class PIIDetection
{
    public PIIType Type { get; set; }
    public string Value { get; set; } = string.Empty;
    public int StartIndex { get; set; }
    public int Length { get; set; }
    public double Confidence { get; set; }
    public string FieldName { get; set; } = string.Empty;
}

/// <summary>
/// Interface for detecting PII in strings
/// </summary>
public interface IPIIDetector
{
    /// <summary>
    /// Detects PII in the given text
    /// </summary>
    IEnumerable<PIIDetection> DetectPII(string text, string fieldName = "");

    /// <summary>
    /// Checks if the given text contains any PII
    /// </summary>
    bool ContainsPII(string text);

    /// <summary>
    /// Gets the configured PII types to detect
    /// </summary>
    PIIType EnabledTypes { get; }
}

/// <summary>
/// Regex-based PII detector implementation
/// </summary>
public class RegexPIIDetector : IPIIDetector
{
    private readonly Dictionary<PIIType, List<PIIPattern>> _patterns;
    private readonly double _confidenceThreshold;

    public PIIType EnabledTypes { get; }

    public RegexPIIDetector(PIIType enabledTypes = PIIType.All, double confidenceThreshold = 0.7)
    {
        EnabledTypes = enabledTypes;
        _confidenceThreshold = confidenceThreshold;
        _patterns = InitializePatterns();
    }

    public IEnumerable<PIIDetection> DetectPII(string text, string fieldName = "")
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (var (type, patterns) in _patterns)
        {
            if (!EnabledTypes.HasFlag(type))
                continue;

            foreach (var pattern in patterns)
            {
                var matches = pattern.Regex.Matches(text);
                foreach (Match match in matches)
                {
                    if (match.Success && pattern.Confidence >= _confidenceThreshold)
                    {
                        yield return new PIIDetection
                        {
                            Type = type,
                            Value = match.Value,
                            StartIndex = match.Index,
                            Length = match.Length,
                            Confidence = pattern.Confidence,
                            FieldName = fieldName
                        };
                    }
                }
            }
        }
    }

    public bool ContainsPII(string text)
    {
        return DetectPII(text).Any();
    }

    private Dictionary<PIIType, List<PIIPattern>> InitializePatterns()
    {
        return new Dictionary<PIIType, List<PIIPattern>>
        {
            [PIIType.EmailAddress] = new()
            {
                new PIIPattern(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", 0.95)
            },
            [PIIType.PhoneNumber] = new()
            {
                new PIIPattern(@"\b\d{3}[-.]?\d{3}[-.]?\d{4}\b", 0.85),
                new PIIPattern(@"\b\(\d{3}\)\s?\d{3}[-.]?\d{4}\b", 0.90),
                new PIIPattern(@"\b\+1[-.]?\d{3}[-.]?\d{3}[-.]?\d{4}\b", 0.90)
            },
            [PIIType.SocialSecurityNumber] = new()
            {
                new PIIPattern(@"\b\d{3}[-]?\d{2}[-]?\d{4}\b", 0.80),
                new PIIPattern(@"\b\d{3}\s\d{2}\s\d{4}\b", 0.85)
            },
            [PIIType.CreditCardNumber] = new()
            {
                // Visa, MasterCard, American Express, Discover
                new PIIPattern(@"\b4\d{3}[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}\b", 0.90),
                new PIIPattern(@"\b5[1-5]\d{2}[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}\b", 0.90),
                new PIIPattern(@"\b3[47]\d{2}[-\s]?\d{6}[-\s]?\d{5}\b", 0.90),
                new PIIPattern(@"\b6011[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}\b", 0.90)
            },
            [PIIType.IPAddress] = new()
            {
                new PIIPattern(@"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b", 0.95),
                new PIIPattern(@"\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b", 0.95)
            },
            [PIIType.PersonName] = new()
            {
                // Simple name detection - can be enhanced with NLP
                new PIIPattern(@"\b[A-Z][a-z]+\s[A-Z][a-z]+\b", 0.60)
            },
            [PIIType.DateOfBirth] = new()
            {
                new PIIPattern(@"\b\d{1,2}[/\-]\d{1,2}[/\-]\d{4}\b", 0.70),
                new PIIPattern(@"\b\d{4}[/\-]\d{1,2}[/\-]\d{1,2}\b", 0.70)
            }
        };
    }

    private class PIIPattern
    {
        public Regex Regex { get; }
        public double Confidence { get; }

        public PIIPattern(string pattern, double confidence)
        {
            Regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Confidence = confidence;
        }
    }
}

/// <summary>
/// Interface for redacting PII from strings
/// </summary>
public interface IPIIRedactor
{
    /// <summary>
    /// Redacts PII from the given text
    /// </summary>
    string RedactPII(string text, string fieldName = "");

    /// <summary>
    /// Redacts detected PII using the specified strategy
    /// </summary>
    string RedactDetectedPII(string text, IEnumerable<PIIDetection> detections, PIIRedactionStrategy strategy);
}

/// <summary>
/// Default PII redactor implementation
/// </summary>
public class PIIRedactor : IPIIRedactor
{
    private readonly IPIIDetector _detector;
    private readonly PIIRedactionStrategy _defaultStrategy;
    private readonly Dictionary<PIIType, PIIRedactionStrategy> _typeStrategies;

    public PIIRedactor(IPIIDetector detector, PIIRedactionStrategy defaultStrategy = PIIRedactionStrategy.Mask)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _defaultStrategy = defaultStrategy;
        _typeStrategies = new Dictionary<PIIType, PIIRedactionStrategy>();
    }

    public void SetStrategyForType(PIIType type, PIIRedactionStrategy strategy)
    {
        _typeStrategies[type] = strategy;
    }

    public string RedactPII(string text, string fieldName = "")
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var detections = _detector.DetectPII(text, fieldName).ToList();
        if (!detections.Any())
            return text;

        return RedactDetectedPII(text, detections, _defaultStrategy);
    }

    public string RedactDetectedPII(string text, IEnumerable<PIIDetection> detections, PIIRedactionStrategy strategy)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = text;
        var offset = 0;

        foreach (var detection in detections.OrderBy(d => d.StartIndex))
        {
            var effectiveStrategy = _typeStrategies.TryGetValue(detection.Type, out var typeStrategy)
                ? typeStrategy
                : strategy;

            var replacement = CreateReplacement(detection.Value, effectiveStrategy);
            var startIndex = detection.StartIndex + offset;

            result = result.Substring(0, startIndex) + replacement + result.Substring(startIndex + detection.Length);
            offset += replacement.Length - detection.Length;
        }

        return result;
    }

    private static string CreateReplacement(string originalValue, PIIRedactionStrategy strategy)
    {
        return strategy switch
        {
            PIIRedactionStrategy.Remove => "",
            PIIRedactionStrategy.Mask => new string('*', Math.Min(originalValue.Length, 8)),
            PIIRedactionStrategy.Hash => $"<hash:{ComputeSimpleHash(originalValue):x8}>",
            PIIRedactionStrategy.PartialMask => CreatePartialMask(originalValue),
            PIIRedactionStrategy.Placeholder => "<REDACTED>",
            _ => "<REDACTED>"
        };
    }

    private static string CreatePartialMask(string value)
    {
        if (value.Length <= 3)
            return new string('*', value.Length);

        if (value.Length <= 6)
            return value[0] + new string('*', value.Length - 2) + value[^1];

        return value[..2] + new string('*', value.Length - 4) + value[^2..];
    }

    private static int ComputeSimpleHash(string input)
    {
        return input.GetHashCode();
    }
}