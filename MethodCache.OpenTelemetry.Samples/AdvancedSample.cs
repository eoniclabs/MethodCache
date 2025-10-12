using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MethodCache.Core;
using MethodCache.Core;
using MethodCache.OpenTelemetry.Correlation;
using MethodCache.OpenTelemetry.Exporters;
using MethodCache.OpenTelemetry.HotReload;
using MethodCache.OpenTelemetry.Security;

namespace MethodCache.OpenTelemetry.Samples;

/// <summary>
/// Advanced sample showing enhanced OpenTelemetry features
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AdvancedSampleController : ControllerBase
{
    private readonly IAdvancedUserService _userService;
    private readonly IAdvancedCorrelationManager _correlationManager;
    private readonly IConfigurationReloadManager _configManager;
    private readonly ILogger<AdvancedSampleController> _logger;

    public AdvancedSampleController(
        IAdvancedUserService userService,
        IAdvancedCorrelationManager correlationManager,
        IConfigurationReloadManager configManager,
        ILogger<AdvancedSampleController> logger)
    {
        _userService = userService;
        _correlationManager = correlationManager;
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>
    /// Example showing advanced correlation with database operations
    /// </summary>
    [HttpGet("users/{id}")]
    public async Task<IActionResult> GetUser(int id)
    {
        // Start correlation context for this operation
        var correlation = _correlationManager.StartCorrelation(
            CorrelationType.HttpRequest,
            $"GetUser-{id}");

        try
        {
            // Set additional correlation properties
            _correlationManager.SetProperty("user.requested_id", id);
            _correlationManager.SetProperty("operation.priority", "high");
            _correlationManager.AddTag("user-lookup");

            // This will automatically correlate cache operations with database queries
            var user = await _userService.GetUserWithProfileAsync(id);

            if (user == null)
            {
                _correlationManager.AddTag("not-found");
                return NotFound();
            }

            _correlationManager.SetProperty("user.found", true);
            _correlationManager.AddTag("success");

            return Ok(user);
        }
        catch (System.Exception ex)
        {
            _correlationManager.SetProperty("error", true);
            _correlationManager.AddTag("error");
            _logger.LogError(ex, "Error getting user {UserId}", id);
            throw;
        }
        finally
        {
            _correlationManager.EndCorrelation(correlation.CorrelationId);
        }
    }

    /// <summary>
    /// Example showing configuration hot reload
    /// </summary>
    [HttpPost("config/reload")]
    public async Task<IActionResult> ReloadConfiguration()
    {
        try
        {
            await _configManager.ReloadFromSourceAsync(HttpContext.RequestAborted);
            return Ok(new { message = "Configuration reloaded successfully" });
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to reload configuration");
            return StatusCode(500, new { error = "Configuration reload failed" });
        }
    }

    /// <summary>
    /// Example showing runtime configuration updates
    /// </summary>
    [HttpPut("config/sampling-ratio")]
    public async Task<IActionResult> UpdateSamplingRatio([FromBody] UpdateSamplingRatioRequest request)
    {
        var validationResult = await _configManager.ValidateConfigurationAsync(
            HotReload.ConfigurationSection.OpenTelemetry,
            new Configuration.OpenTelemetryOptions { SamplingRatio = request.SamplingRatio });

        if (!validationResult.IsValid)
        {
            return BadRequest(new { errors = validationResult.Errors });
        }

        var success = await _configManager.UpdatePropertyAsync(
            HotReload.ConfigurationSection.OpenTelemetry,
            "SamplingRatio",
            request.SamplingRatio);

        if (success)
        {
            return Ok(new { message = $"Sampling ratio updated to {request.SamplingRatio}" });
        }

        return StatusCode(500, new { error = "Failed to update sampling ratio" });
    }

    /// <summary>
    /// Example showing configuration history
    /// </summary>
    [HttpGet("config/history")]
    public IActionResult GetConfigurationHistory()
    {
        var history = _configManager.GetConfigurationHistory(HotReload.ConfigurationSection.OpenTelemetry);
        return Ok(history);
    }

    /// <summary>
    /// Example showing active correlations
    /// </summary>
    [HttpGet("correlations")]
    public IActionResult GetActiveCorrelations()
    {
        var correlations = _correlationManager.GetActiveCorrelations();
        return Ok(correlations.Select(c => new
        {
            c.CorrelationId,
            c.Type,
            c.OperationName,
            c.StartTime,
            c.UserId,
            c.Tags,
            Properties = c.Properties.Where(kvp => !IsSensitiveProperty(kvp.Key))
        }));
    }

    private static bool IsSensitiveProperty(string propertyName)
    {
        var sensitiveProperties = new[] { "password", "secret", "token", "key" };
        return sensitiveProperties.Any(sp => propertyName.Contains(sp, System.StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Advanced user service with correlation and security features
/// </summary>
public interface IAdvancedUserService
{
    [Cache(Duration = "00:05:00", Tags = new[] { "user", "profile" })]
    Task<AdvancedUser?> GetUserWithProfileAsync(int userId);

    [Cache(Duration = "00:10:00", Tags = new[] { "user", "preferences" })]
    Task<UserPreferences?> GetUserPreferencesAsync(int userId);

    [CacheInvalidate(Tags = new[] { "user" })]
    Task UpdateUserAsync(int userId, AdvancedUser user);
}

public class AdvancedUserService : IAdvancedUserService
{
    private readonly IAdvancedCorrelationManager _correlationManager;
    private readonly ILogger<AdvancedUserService> _logger;
    private readonly Dictionary<int, AdvancedUser> _users = new()
    {
        [1] = new AdvancedUser
        {
            Id = 1,
            Name = "John Doe",
            Email = "john.doe@example.com",
            Phone = "555-123-4567",
            Profile = new UserProfile { Bio = "Software Developer", Location = "San Francisco" }
        },
        [2] = new AdvancedUser
        {
            Id = 2,
            Name = "Jane Smith",
            Email = "jane.smith@example.com",
            Phone = "555-987-6543",
            Profile = new UserProfile { Bio = "Product Manager", Location = "New York" }
        }
    };

    public AdvancedUserService(
        IAdvancedCorrelationManager correlationManager,
        ILogger<AdvancedUserService> logger)
    {
        _correlationManager = correlationManager;
        _logger = logger;
    }

    public async Task<AdvancedUser?> GetUserWithProfileAsync(int userId)
    {
        // Simulate database query correlation
        var query = $"SELECT u.*, p.* FROM Users u LEFT JOIN Profiles p ON u.Id = p.UserId WHERE u.Id = {userId}";
        _correlationManager.CorrelateCacheWithDatabase($"user:{userId}:profile", query);

        // Simulate database delay
        await Task.Delay(200);

        _logger.LogInformation("Retrieved user {UserId} with profile", userId);
        return _users.TryGetValue(userId, out var user) ? user : null;
    }

    public async Task<UserPreferences?> GetUserPreferencesAsync(int userId)
    {
        // Simulate external service call
        _correlationManager.CorrelateCacheWithExternalService(
            $"user:{userId}:preferences",
            "UserPreferencesService",
            "https://api.preferences.example.com/users/preferences");

        await Task.Delay(100);

        return new UserPreferences
        {
            UserId = userId,
            Theme = "dark",
            Language = "en-US",
            Notifications = true
        };
    }

    public async Task UpdateUserAsync(int userId, AdvancedUser user)
    {
        var correlation = _correlationManager.StartCorrelation(
            CorrelationType.DatabaseQuery,
            $"UpdateUser-{userId}");

        try
        {
            _correlationManager.SetProperty("db.operation", "update");
            _correlationManager.SetProperty("user.id", userId);

            if (_users.ContainsKey(userId))
            {
                _users[userId] = user;
                _logger.LogInformation("Updated user {UserId}", userId);
            }

            await Task.Delay(150); // Simulate database operation
        }
        finally
        {
            _correlationManager.EndCorrelation(correlation.CorrelationId);
        }
    }
}

/// <summary>
/// Custom metrics exporter example
/// </summary>
public class CustomLoggingMetricsExporter : CacheMetricsExporterBase
{
    private readonly ILogger<CustomLoggingMetricsExporter> _logger;

    public CustomLoggingMetricsExporter(
        ILogger<CustomLoggingMetricsExporter> logger,
        CacheExporterOptions options) : base(options)
    {
        _logger = logger;
    }

    public override string Name => "CustomLogging";

    public override IReadOnlySet<MetricType> SupportedTypes =>
        new HashSet<MetricType> { MetricType.Counter, MetricType.Histogram, MetricType.Gauge };

    public override async Task<ExportResult> ExportAsync(IEnumerable<CacheMetricData> batch, CancellationToken cancellationToken = default)
    {
        ThrowIfShutdown();

        try
        {
            foreach (var metric in batch)
            {
                _logger.LogInformation(
                    "Metric: {Name}={Value} {Unit} [{Type}] @{Timestamp} Tags: {Tags}",
                    metric.Name,
                    metric.Value,
                    metric.Unit,
                    metric.Type,
                    metric.Timestamp,
                    string.Join(", ", metric.Tags.Select(kvp => $"{kvp.Key}={kvp.Value}"))
                );
            }

            await Task.CompletedTask;
            return ExportResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExportResult.Failure;
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Error exporting metrics");
            return ExportResult.Failure;
        }
    }
}

// Models
public class AdvancedUser
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public UserProfile Profile { get; set; } = new();
}

public class UserProfile
{
    public string Bio { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
}

public class UserPreferences
{
    public int UserId { get; set; }
    public string Theme { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public bool Notifications { get; set; }
}

public class UpdateSamplingRatioRequest
{
    public double SamplingRatio { get; set; }
}