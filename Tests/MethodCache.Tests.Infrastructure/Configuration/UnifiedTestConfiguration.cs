using System;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Tests.Infrastructure.Environment;

namespace MethodCache.Tests.Infrastructure.Configuration;

/// <summary>
/// Unified configuration system that combines environment detection, secure configuration, and intelligent defaults
/// </summary>
public class UnifiedTestConfiguration
{
    private readonly SecureConfigurationManager _configManager;
    private TestEnvironmentInfo? _environmentInfo;
    private TestConfiguration? _testConfiguration;

    public UnifiedTestConfiguration(SecureConfigurationManager? configManager = null)
    {
        _configManager = configManager ?? new SecureConfigurationManager();
    }

    /// <summary>
    /// Initializes the test configuration by detecting environment and loading saved settings
    /// </summary>
    public async Task<TestConfigurationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Detect current environment
        _environmentInfo = await TestEnvironmentDetector.DetectAsync(cancellationToken);

        // Load or create test configuration
        _testConfiguration = await _configManager.GetTestConfigurationAsync();

        // Validate and update configuration based on environment
        var result = await ValidateAndUpdateConfigurationAsync();

        return result;
    }

    /// <summary>
    /// Gets the current test configuration (call InitializeAsync first)
    /// </summary>
    public TestConfiguration GetConfiguration()
    {
        if (_testConfiguration == null)
        {
            throw new InvalidOperationException("Configuration not initialized. Call InitializeAsync first.");
        }

        return _testConfiguration;
    }

    /// <summary>
    /// Gets the current environment information (call InitializeAsync first)
    /// </summary>
    public TestEnvironmentInfo GetEnvironmentInfo()
    {
        if (_environmentInfo == null)
        {
            throw new InvalidOperationException("Environment not detected. Call InitializeAsync first.");
        }

        return _environmentInfo;
    }

    /// <summary>
    /// Gets optimized connection strings based on environment and configuration
    /// </summary>
    public async Task<OptimizedConnectionStrings> GetOptimizedConnectionStringsAsync()
    {
        if (_environmentInfo == null || _testConfiguration == null)
        {
            await InitializeAsync();
        }

        var result = new OptimizedConnectionStrings();

        // SQL Server connection
        if (_environmentInfo!.SqlServer.IsAvailable && !string.IsNullOrEmpty(_environmentInfo.SqlServer.ConnectionString))
        {
            result.SqlServer = _environmentInfo.SqlServer.ConnectionString;
            result.SqlServerSource = "External (detected)";
        }
        else if (!string.IsNullOrEmpty(_testConfiguration!.SqlServer.ConnectionString))
        {
            result.SqlServer = _testConfiguration.SqlServer.ConnectionString;
            result.SqlServerSource = "Configuration";
        }
        else
        {
            result.SqlServer = null; // Will use Docker
            result.SqlServerSource = "Docker container";
        }

        // Redis connection
        if (_environmentInfo.Redis.IsAvailable && !string.IsNullOrEmpty(_environmentInfo.Redis.ConnectionString))
        {
            result.Redis = _environmentInfo.Redis.ConnectionString;
            result.RedisSource = "External (detected)";
        }
        else if (!string.IsNullOrEmpty(_testConfiguration.Redis.ConnectionString))
        {
            result.Redis = _testConfiguration.Redis.ConnectionString;
            result.RedisSource = "Configuration";
        }
        else
        {
            result.Redis = null; // Will use Docker
            result.RedisSource = "Docker container";
        }

        return result;
    }

    /// <summary>
    /// Sets up a secure configuration value (encrypted storage)
    /// </summary>
    public Task SetSecureAsync(string key, string value)
    {
        return _configManager.SetSecureAsync(key, value);
    }

    /// <summary>
    /// Sets up a plain configuration value
    /// </summary>
    public Task SetAsync(string key, string value)
    {
        return _configManager.SetAsync(key, value);
    }

    /// <summary>
    /// Gets a configuration value with fallbacks
    /// </summary>
    public Task<string?> GetWithFallbackAsync(string key, params string[] environmentVariables)
    {
        return _configManager.GetWithFallbackAsync(key, environmentVariables);
    }

    /// <summary>
    /// Interactive setup wizard for first-time configuration
    /// </summary>
    public async Task<bool> RunSetupWizardAsync(bool interactive = true)
    {
        if (_environmentInfo == null)
        {
            await InitializeAsync();
        }

        Console.WriteLine("🧪 MethodCache Test Environment Setup Wizard");
        Console.WriteLine("===========================================");
        Console.WriteLine();

        // Show current environment status
        ShowEnvironmentStatus();

        if (!interactive)
        {
            return await AutoConfigureAsync();
        }

        return await InteractiveConfigureAsync();
    }

    /// <summary>
    /// Validates the current configuration and provides recommendations
    /// </summary>
    public async Task<ConfigurationValidationResult> ValidateConfigurationAsync()
    {
        if (_environmentInfo == null || _testConfiguration == null)
        {
            await InitializeAsync();
        }

        var result = new ConfigurationValidationResult();
        var connections = await GetOptimizedConnectionStringsAsync();

        // Check SQL Server
        if (connections.SqlServer != null)
        {
            try
            {
                using var connection = new Microsoft.Data.SqlClient.SqlConnection(connections.SqlServer);
                await connection.OpenAsync();
                result.SqlServerValid = true;
                result.SqlServerMessage = $"✅ Connected via {connections.SqlServerSource}";
            }
            catch (Exception ex)
            {
                result.SqlServerValid = false;
                result.SqlServerMessage = $"❌ Connection failed: {ex.Message}";
            }
        }
        else
        {
            result.SqlServerValid = _environmentInfo!.Docker.IsInstalled && _environmentInfo.Docker.IsRunning;
            result.SqlServerMessage = result.SqlServerValid
                ? "✅ Will use Docker container"
                : "❌ Docker not available";
        }

        // Check Redis
        if (connections.Redis != null)
        {
            try
            {
                using var connection = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(connections.Redis);
                await connection.GetDatabase().PingAsync();
                result.RedisValid = true;
                result.RedisMessage = $"✅ Connected via {connections.RedisSource}";
                await connection.CloseAsync();
            }
            catch (Exception ex)
            {
                result.RedisValid = false;
                result.RedisMessage = $"❌ Connection failed: {ex.Message}";
            }
        }
        else
        {
            result.RedisValid = _environmentInfo!.Docker.IsInstalled && _environmentInfo.Docker.IsRunning;
            result.RedisMessage = result.RedisValid
                ? "✅ Will use Docker container"
                : "❌ Docker not available";
        }

        result.IsValid = result.SqlServerValid && result.RedisValid;
        result.PerformanceEstimate = EstimatePerformance(connections);

        return result;
    }

    private async Task<TestConfigurationResult> ValidateAndUpdateConfigurationAsync()
    {
        var result = new TestConfigurationResult
        {
            EnvironmentInfo = _environmentInfo!,
            Configuration = _testConfiguration!,
            IsValid = _environmentInfo.IsValid
        };

        // Update configuration based on detected environment
        if (_environmentInfo.Redis.IsAvailable && string.IsNullOrEmpty(_testConfiguration!.Redis.ConnectionString))
        {
            _testConfiguration.Redis.ConnectionString = _environmentInfo.Redis.ConnectionString;
            await _configManager.SetAsync("redis_connection", _environmentInfo.Redis.ConnectionString);
        }

        if (_environmentInfo.SqlServer.IsAvailable && string.IsNullOrEmpty(_testConfiguration.SqlServer.ConnectionString))
        {
            _testConfiguration.SqlServer.ConnectionString = _environmentInfo.SqlServer.ConnectionString;
            await _configManager.SetAsync("sqlserver_connection", _environmentInfo.SqlServer.ConnectionString);
        }

        // Generate recommendations
        result.Recommendations = GenerateRecommendations();

        return result;
    }

    private void ShowEnvironmentStatus()
    {
        Console.WriteLine($"Platform: {_environmentInfo!.Platform} ({_environmentInfo.Architecture})");
        Console.WriteLine($"Docker: {(_environmentInfo.Docker.IsInstalled ? "✅ Installed" : "❌ Not found")} " +
                         $"{(_environmentInfo.Docker.IsRunning ? "(Running)" : "(Not running)")}");
        Console.WriteLine($"Redis: {(_environmentInfo.Redis.IsAvailable ? "✅ Available" : "❌ Not available")} " +
                         $"{(_environmentInfo.Redis.IsAvailable ? $"v{_environmentInfo.Redis.Version}" : "")}");
        Console.WriteLine($"SQL Server: {(_environmentInfo.SqlServer.IsAvailable ? "✅ Available" : "❌ Not available")} " +
                         $"{(_environmentInfo.SqlServer.IsAvailable ? $"v{_environmentInfo.SqlServer.Version}" : "")}");
        Console.WriteLine();

        // Show recommendations
        Console.WriteLine("Recommendations:");
        foreach (var instruction in _environmentInfo.SetupInstructions)
        {
            Console.WriteLine($"  {instruction}");
        }
        Console.WriteLine();
    }

    private async Task<bool> AutoConfigureAsync()
    {
        // Automatically set up the best available configuration
        await _configManager.InitializeDefaultsAsync();

        // Set Docker preferences based on environment
        if (_environmentInfo!.Docker.IsInstalled && _environmentInfo.Docker.IsRunning)
        {
            await _configManager.SetAsync("docker_use_containers", "true");
            await _configManager.SetAsync("docker_reuse", "true");
            await _configManager.SetAsync("docker_timeout", "00:05:00");
        }

        Console.WriteLine("✅ Auto-configuration completed");
        return true;
    }

    private async Task<bool> InteractiveConfigureAsync()
    {
        // Interactive setup - ask user for preferences
        Console.WriteLine("Would you like to configure test settings? (y/n): ");
        var response = Console.ReadLine();

        if (response?.ToLower() != "y")
        {
            return await AutoConfigureAsync();
        }

        // SQL Server setup
        if (!_environmentInfo!.SqlServer.IsAvailable)
        {
            Console.WriteLine("\n🗄️ SQL Server Configuration:");
            Console.WriteLine("1. Use Docker container (recommended)");
            Console.WriteLine("2. Set up external SQL Server connection");
            Console.Write("Choose option (1-2): ");

            var sqlChoice = Console.ReadLine();
            if (sqlChoice == "2")
            {
                Console.Write("Enter SQL Server connection string: ");
                var sqlConnection = Console.ReadLine();
                if (!string.IsNullOrEmpty(sqlConnection))
                {
                    await _configManager.SetSecureAsync("sqlserver_connection", sqlConnection);
                }
            }
        }

        // Redis setup
        if (!_environmentInfo.Redis.IsAvailable)
        {
            Console.WriteLine("\n🔄 Redis Configuration:");
            Console.WriteLine("1. Use Docker container (recommended)");
            Console.WriteLine("2. Set up external Redis connection");
            Console.Write("Choose option (1-2): ");

            var redisChoice = Console.ReadLine();
            if (redisChoice == "2")
            {
                Console.Write("Enter Redis connection string (e.g., localhost:6379): ");
                var redisConnection = Console.ReadLine();
                if (!string.IsNullOrEmpty(redisConnection))
                {
                    await _configManager.SetAsync("redis_connection", redisConnection);
                }
            }
        }

        // GitHub setup (for PR testing)
        Console.WriteLine("\n🐙 GitHub Configuration (optional):");
        Console.Write("Enter GitHub token for PR testing (leave empty to skip): ");
        var githubToken = Console.ReadLine();
        if (!string.IsNullOrEmpty(githubToken))
        {
            await _configManager.SetSecureAsync("github_token", githubToken);
        }

        Console.WriteLine("\n✅ Interactive configuration completed");
        return true;
    }

    private List<string> GenerateRecommendations()
    {
        var recommendations = new List<string>();

        var connections = GetOptimizedConnectionStringsAsync().Result;

        if (connections.SqlServerSource == "Docker container" && connections.RedisSource == "Docker container")
        {
            recommendations.Add("🐋 Using Docker for both services - expect 3-5 minute first startup");
            recommendations.Add("⚡ Consider setting up external services for faster iteration");
        }
        else if (connections.SqlServerSource != "Docker container" && connections.RedisSource != "Docker container")
        {
            recommendations.Add("🚀 Optimal setup - using external services for fast test execution");
        }
        else
        {
            recommendations.Add("🔄 Hybrid setup - consider setting up the remaining external service");
        }

        if (_environmentInfo!.Platform == "macOS" && _environmentInfo.Architecture == "Arm64")
        {
            if (!_environmentInfo.Docker.DockerDesktop.RosettaEnabled)
            {
                recommendations.Add("🍎 Enable Rosetta in Docker Desktop for better SQL Server performance");
            }
        }

        return recommendations;
    }

    private static string EstimatePerformance(OptimizedConnectionStrings connections)
    {
        var dockerServices = 0;
        if (connections.SqlServerSource == "Docker container") dockerServices++;
        if (connections.RedisSource == "Docker container") dockerServices++;

        return dockerServices switch
        {
            0 => "🚀 Excellent (~30 seconds)",
            1 => "⚡ Good (~2 minutes)",
            2 => "🐋 Moderate (~5 minutes first run, ~1 minute subsequent)",
            _ => "❓ Unknown"
        };
    }
}

public class TestConfigurationResult
{
    public TestEnvironmentInfo EnvironmentInfo { get; set; } = new();
    public TestConfiguration Configuration { get; set; } = new();
    public bool IsValid { get; set; }
    public List<string> Recommendations { get; set; } = new();
}

public class OptimizedConnectionStrings
{
    public string? SqlServer { get; set; }
    public string SqlServerSource { get; set; } = string.Empty;
    public string? Redis { get; set; }
    public string RedisSource { get; set; } = string.Empty;
}

public class ConfigurationValidationResult
{
    public bool IsValid { get; set; }
    public bool SqlServerValid { get; set; }
    public string SqlServerMessage { get; set; } = string.Empty;
    public bool RedisValid { get; set; }
    public string RedisMessage { get; set; } = string.Empty;
    public string PerformanceEstimate { get; set; } = string.Empty;
}