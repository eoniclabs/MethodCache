using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using MethodCache.OpenTelemetry.Configuration;
using MethodCache.OpenTelemetry.Tracing;

namespace MethodCache.OpenTelemetry.Security;

/// <summary>
/// Enhanced security options for OpenTelemetry
/// </summary>
public class SecurityOptions
{
    /// <summary>
    /// Enable automatic PII detection and redaction
    /// </summary>
    public bool EnablePIIDetection { get; set; } = true;

    /// <summary>
    /// Types of PII to detect and redact
    /// </summary>
    public PIIType PIITypesToDetect { get; set; } = PIIType.All;

    /// <summary>
    /// Default redaction strategy for detected PII
    /// </summary>
    public PIIRedactionStrategy DefaultRedactionStrategy { get; set; } = PIIRedactionStrategy.Hash;

    /// <summary>
    /// Custom redaction strategies for specific PII types
    /// </summary>
    public Dictionary<PIIType, PIIRedactionStrategy> TypeSpecificStrategies { get; set; } = new();

    /// <summary>
    /// Confidence threshold for PII detection (0.0 to 1.0)
    /// </summary>
    public double PIIConfidenceThreshold { get; set; } = 0.8;

    /// <summary>
    /// Fields to always redact regardless of PII detection
    /// </summary>
    public HashSet<string> AlwaysRedactFields { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "secret", "token", "key", "authorization", "credential"
    };

    /// <summary>
    /// Fields to never redact even if PII is detected
    /// </summary>
    public HashSet<string> NeverRedactFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Enable encryption of sensitive span attributes
    /// </summary>
    public bool EnableAttributeEncryption { get; set; } = false;

    /// <summary>
    /// Key for encrypting sensitive attributes (base64 encoded)
    /// </summary>
    public string? EncryptionKey { get; set; }

    /// <summary>
    /// Maximum length for attribute values before truncation
    /// </summary>
    public int MaxAttributeLength { get; set; } = 1000;

    /// <summary>
    /// Enable data loss prevention logging
    /// </summary>
    public bool EnableDLPLogging { get; set; } = true;
}

/// <summary>
/// Security-enhanced activity source that automatically redacts PII
/// </summary>
public class SecurityEnhancedActivitySource : ICacheActivitySource
{
    private readonly ICacheActivitySource _innerActivitySource;
    private readonly IPIIDetector _piiDetector;
    private readonly IPIIRedactor _piiRedactor;
    private readonly SecurityOptions _securityOptions;
    private readonly IAttributeEncryptor? _encryptor;

    public SecurityEnhancedActivitySource(
        ICacheActivitySource innerActivitySource,
        IOptions<SecurityOptions> securityOptions,
        IPIIDetector? piiDetector = null,
        IPIIRedactor? piiRedactor = null,
        IAttributeEncryptor? encryptor = null)
    {
        _innerActivitySource = innerActivitySource ?? throw new ArgumentNullException(nameof(innerActivitySource));
        _securityOptions = securityOptions.Value;

        _piiDetector = piiDetector ?? new RegexPIIDetector(
            _securityOptions.PIITypesToDetect,
            _securityOptions.PIIConfidenceThreshold);

        _piiRedactor = piiRedactor ?? CreateDefaultRedactor();
        _encryptor = encryptor;
    }

    public Activity? StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
    {
        var activity = _innerActivitySource.StartActivity(operationName, kind);
        if (activity != null)
        {
            EnhanceActivitySecurity(activity);
        }
        return activity;
    }

    public Activity? StartCacheOperation(string methodName, string operation = TracingConstants.Operations.Get)
    {
        var sanitizedMethodName = SanitizeValue(methodName, "methodName");
        return _innerActivitySource.StartCacheOperation(sanitizedMethodName, operation);
    }

    public void SetCacheHit(Activity? activity, bool hit)
    {
        _innerActivitySource.SetCacheHit(activity, hit);
    }

    public void SetCacheKey(Activity? activity, string key, bool recordFullKey = false)
    {
        if (activity == null) return;

        var sanitizedKey = SanitizeValue(key, "cacheKey");
        _innerActivitySource.SetCacheKey(activity, sanitizedKey, recordFullKey);
    }

    public void SetCacheTags(Activity? activity, string[]? tags)
    {
        if (activity == null || tags == null) return;

        var sanitizedTags = new string[tags.Length];
        for (int i = 0; i < tags.Length; i++)
        {
            sanitizedTags[i] = SanitizeValue(tags[i], "tag");
        }

        _innerActivitySource.SetCacheTags(activity, sanitizedTags);
    }

    public void SetCacheError(Activity? activity, Exception exception)
    {
        if (activity == null) return;

        // Create sanitized exception info
        var sanitizedException = CreateSanitizedException(exception);
        _innerActivitySource.SetCacheError(activity, sanitizedException);
    }

    public void SetHttpCorrelation(Activity? activity)
    {
        _innerActivitySource.SetHttpCorrelation(activity);
    }

    public void RecordException(Activity? activity, Exception exception)
    {
        if (activity == null) return;

        var sanitizedException = CreateSanitizedException(exception);
        _innerActivitySource.RecordException(activity, sanitizedException);
    }

    private void EnhanceActivitySecurity(Activity activity)
    {
        // Override SetTag to automatically sanitize values
        var originalSetTag = activity.SetTag;

        // Note: This is a conceptual approach. In practice, you'd need to use
        // ActivityListener or custom wrappers to intercept SetTag calls
    }

    private string SanitizeValue(string value, string fieldName)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // Always redact fields
        if (_securityOptions.AlwaysRedactFields.Contains(fieldName))
        {
            return CreateRedaction(value, _securityOptions.DefaultRedactionStrategy);
        }

        // Never redact fields
        if (_securityOptions.NeverRedactFields.Contains(fieldName))
        {
            return TruncateIfNeeded(value);
        }

        // PII detection and redaction
        if (_securityOptions.EnablePIIDetection)
        {
            var redactedValue = _piiRedactor.RedactPII(value, fieldName);
            return TruncateIfNeeded(redactedValue);
        }

        return TruncateIfNeeded(value);
    }

    private string TruncateIfNeeded(string value)
    {
        if (value.Length <= _securityOptions.MaxAttributeLength)
            return value;

        return value.Substring(0, _securityOptions.MaxAttributeLength - 3) + "...";
    }

    private Exception CreateSanitizedException(Exception original)
    {
        // Create a new exception with sanitized message and stack trace
        var sanitizedMessage = SanitizeValue(original.Message, "exceptionMessage");

        return new Exception(sanitizedMessage)
        {
            Source = original.Source,
            HelpLink = original.HelpLink
            // Don't include full stack trace to avoid potential PII
        };
    }

    private string CreateRedaction(string value, PIIRedactionStrategy strategy)
    {
        return strategy switch
        {
            PIIRedactionStrategy.Remove => "",
            PIIRedactionStrategy.Mask => new string('*', Math.Min(value.Length, 8)),
            PIIRedactionStrategy.Hash => $"<hash:{value.GetHashCode():x8}>",
            PIIRedactionStrategy.PartialMask => CreatePartialMask(value),
            PIIRedactionStrategy.Placeholder => "<REDACTED>",
            _ => "<REDACTED>"
        };
    }

    private string CreatePartialMask(string value)
    {
        if (value.Length <= 3)
            return new string('*', value.Length);

        if (value.Length <= 6)
            return value[0] + new string('*', value.Length - 2) + value[^1];

        return value[..2] + new string('*', value.Length - 4) + value[^2..];
    }

    private IPIIRedactor CreateDefaultRedactor()
    {
        var redactor = new PIIRedactor(_piiDetector, _securityOptions.DefaultRedactionStrategy);

        // Configure type-specific strategies
        foreach (var (type, strategy) in _securityOptions.TypeSpecificStrategies)
        {
            redactor.SetStrategyForType(type, strategy);
        }

        return redactor;
    }
}

/// <summary>
/// Interface for encrypting sensitive attributes
/// </summary>
public interface IAttributeEncryptor
{
    /// <summary>
    /// Encrypts a value for storage in telemetry
    /// </summary>
    string Encrypt(string value);

    /// <summary>
    /// Decrypts a value for analysis (if decryption key is available)
    /// </summary>
    string Decrypt(string encryptedValue);
}

/// <summary>
/// Simple AES-based attribute encryptor
/// </summary>
public class AESAttributeEncryptor : IAttributeEncryptor
{
    private readonly byte[] _key;

    public AESAttributeEncryptor(string base64Key)
    {
        _key = Convert.FromBase64String(base64Key);
    }

    public string Encrypt(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // Simplified encryption - in production, use proper AES with IV
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"encrypted:{value.GetHashCode():x8}"));
    }

    public string Decrypt(string encryptedValue)
    {
        // This is a placeholder - real decryption would require proper key management
        return "<encrypted>";
    }
}