using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using MethodCache.Core;
using MethodCache.OpenTelemetry.Extensions;
using MethodCache.OpenTelemetry.Propagators;

var builder = WebApplication.CreateBuilder(args);

// Configure MethodCache
builder.Services.AddMethodCache();

// Add OpenTelemetry instrumentation for MethodCache
builder.Services.AddMethodCacheOpenTelemetry(options =>
{
    options.EnableTracing = true;
    options.EnableMetrics = true;
    options.EnableHttpCorrelation = true;
    options.EnableBaggagePropagation = true;
    options.RecordCacheKeys = true;
    options.HashCacheKeys = true;
    options.ServiceName = "MethodCacheSample";
    options.ServiceVersion = "1.0.0";
    options.Environment = builder.Environment.EnvironmentName;
});

// Configure OpenTelemetry SDK
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService("MethodCacheSample", serviceVersion: "1.0.0")
        .AddAttributes(new Dictionary<string, object>
        {
            ["environment"] = builder.Environment.EnvironmentName,
            ["team"] = "platform"
        }))
    .WithTracing(tracing =>
    {
        tracing.AddMethodCacheInstrumentation()
               .AddAspNetCoreInstrumentation(options =>
               {
                   options.RecordException = true;
               })
               .AddHttpClientInstrumentation();

        // Add console exporter for demo
        if (builder.Environment.IsDevelopment())
        {
            tracing.AddConsoleExporter();
        }

        // Add OTLP exporter for production
        tracing.AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(builder.Configuration["OpenTelemetry:Endpoint"] ?? "http://localhost:4317");
        });
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMethodCacheInstrumentation()
               .AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation()
               .AddRuntimeInstrumentation()
               .AddProcessInstrumentation();

        // Add Prometheus exporter
        metrics.AddPrometheusExporter();

        // Add console exporter for demo
        if (builder.Environment.IsDevelopment())
        {
            metrics.AddConsoleExporter();
        }
    });

// Add services
builder.Services.AddScoped<IWeatherService, WeatherService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers();

var app = builder.Build();

// Enable HTTP correlation middleware
app.UseMethodCacheHttpCorrelation();

// Prometheus metrics endpoint
app.UseOpenTelemetryPrometheusScrapingEndpoint();

app.MapControllers();

// Sample endpoints
app.MapGet("/", () => "MethodCache OpenTelemetry Sample - Visit /weather or /users/{id}");

app.Run();

// Sample Services
public interface IWeatherService
{
    [Cache(Duration = "00:01:00", Tags = new[] { "weather", "external" })]
    Task<WeatherForecast[]> GetWeatherAsync(string city);
}

public class WeatherService : IWeatherService
{
    private readonly ILogger<WeatherService> _logger;
    private static readonly string[] Summaries = new[]
    {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

    public WeatherService(ILogger<WeatherService> logger)
    {
        _logger = logger;
    }

    public async Task<WeatherForecast[]> GetWeatherAsync(string city)
    {
        _logger.LogInformation("Fetching weather for {City}", city);

        // Simulate API call
        await Task.Delay(500);

        var forecast = Enumerable.Range(1, 5).Select(index => new WeatherForecast
        {
            Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            TemperatureC = Random.Shared.Next(-20, 55),
            Summary = Summaries[Random.Shared.Next(Summaries.Length)],
            City = city
        }).ToArray();

        return forecast;
    }
}

public interface IUserService
{
    [Cache(Duration = "00:05:00", Tags = new[] { "user", "profile" })]
    Task<User> GetUserAsync(int userId);

    [CacheInvalidate(Tags = new[] { "user" })]
    Task UpdateUserAsync(int userId, string name);
}

public class UserService : IUserService
{
    private readonly ILogger<UserService> _logger;
    private readonly Dictionary<int, User> _users = new()
    {
        [1] = new User { Id = 1, Name = "Alice", Email = "alice@example.com" },
        [2] = new User { Id = 2, Name = "Bob", Email = "bob@example.com" },
        [3] = new User { Id = 3, Name = "Charlie", Email = "charlie@example.com" }
    };

    public UserService(ILogger<UserService> logger)
    {
        _logger = logger;
    }

    public async Task<User> GetUserAsync(int userId)
    {
        _logger.LogInformation("Fetching user {UserId}", userId);

        // Simulate database call
        await Task.Delay(200);

        if (_users.TryGetValue(userId, out var user))
        {
            return user;
        }

        throw new KeyNotFoundException($"User {userId} not found");
    }

    public async Task UpdateUserAsync(int userId, string name)
    {
        _logger.LogInformation("Updating user {UserId} name to {Name}", userId, name);

        await Task.Delay(100);

        if (_users.TryGetValue(userId, out var user))
        {
            user.Name = name;
        }
    }
}

// Controllers
[ApiController]
[Route("[controller]")]
public class WeatherController : ControllerBase
{
    private readonly IWeatherService _weatherService;
    private readonly IBaggagePropagator _baggagePropagator;

    public WeatherController(IWeatherService weatherService, IBaggagePropagator baggagePropagator)
    {
        _weatherService = weatherService;
        _baggagePropagator = baggagePropagator;
    }

    [HttpGet("{city}")]
    public async Task<IActionResult> Get(string city)
    {
        // Set correlation ID for distributed tracing
        var correlationId = HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        _baggagePropagator.SetCacheCorrelationId(correlationId);

        var forecast = await _weatherService.GetWeatherAsync(city);
        return Ok(forecast);
    }
}

[ApiController]
[Route("[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly IBaggagePropagator _baggagePropagator;

    public UsersController(IUserService userService, IBaggagePropagator baggagePropagator)
    {
        _userService = userService;
        _baggagePropagator = baggagePropagator;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetUser(int id)
    {
        try
        {
            // Set user context for tracing
            if (User.Identity?.IsAuthenticated == true)
            {
                _baggagePropagator.SetCacheUserId(User.Identity.Name);
            }

            var user = await _userService.GetUserAsync(id);
            return Ok(user);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest request)
    {
        await _userService.UpdateUserAsync(id, request.Name);
        return NoContent();
    }
}

// Models
public record WeatherForecast
{
    public DateOnly Date { get; init; }
    public int TemperatureC { get; init; }
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
    public string? Summary { get; init; }
    public string? City { get; init; }
}

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class UpdateUserRequest
{
    public string Name { get; set; } = string.Empty;
}