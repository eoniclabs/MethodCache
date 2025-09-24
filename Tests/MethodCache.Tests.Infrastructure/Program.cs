using System;
using System.CommandLine;
using System.Threading.Tasks;
using MethodCache.Tests.Infrastructure.Configuration;
using MethodCache.Tests.Infrastructure.Environment;

namespace MethodCache.Tests.Infrastructure;

/// <summary>
/// Command-line tool for managing MethodCache test environment configuration
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("MethodCache Test Environment Configuration Tool");

        // Setup command
        var setupCommand = new Command("setup", "Set up the test environment configuration");
        var interactiveOption = new Option<bool>("--interactive", () => true, "Run in interactive mode");
        var configPathOption = new Option<string?>("--config-path", "Custom configuration directory path");
        var validateOnlyOption = new Option<bool>("--validate-only", "Only validate, don't make changes");

        setupCommand.AddOption(interactiveOption);
        setupCommand.AddOption(configPathOption);
        setupCommand.AddOption(validateOnlyOption);

        setupCommand.SetHandler(async (interactive, configPath, validateOnly) =>
        {
            try
            {
                var configManager = string.IsNullOrEmpty(configPath)
                    ? new SecureConfigurationManager()
                    : new SecureConfigurationManager(configPath);

                var unifiedConfig = new UnifiedTestConfiguration(configManager);

                if (validateOnly)
                {
                    var validation = await unifiedConfig.ValidateConfigurationAsync();
                    DisplayValidationResults(validation);
                    System.Environment.ExitCode = validation.IsValid ? 0 : 1;
                    return;
                }

                var success = await unifiedConfig.RunSetupWizardAsync(interactive);
                System.Environment.ExitCode = success ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Setup failed: {ex.Message}");
                System.Environment.ExitCode = 1;
            }
        }, interactiveOption, configPathOption, validateOnlyOption);

        // Validate command
        var validateCommand = new Command("validate", "Validate the current test environment");
        validateCommand.SetHandler(async () =>
        {
            try
            {
                var unifiedConfig = new UnifiedTestConfiguration();
                await unifiedConfig.InitializeAsync();

                var envInfo = unifiedConfig.GetEnvironmentInfo();
                var validation = await unifiedConfig.ValidateConfigurationAsync();

                DisplayEnvironmentInfo(envInfo);
                DisplayValidationResults(validation);

                System.Environment.ExitCode = validation.IsValid ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Validation failed: {ex.Message}");
                System.Environment.ExitCode = 1;
            }
        });

        // Config command
        var configCommand = new Command("config", "Manage configuration values");

        var setCommand = new Command("set", "Set a configuration value");
        var setKeyArg = new Argument<string>("key", "Configuration key");
        var setValueArg = new Argument<string>("value", "Configuration value");
        var secureOption = new Option<bool>("--secure", "Store as encrypted value");

        setCommand.AddArgument(setKeyArg);
        setCommand.AddArgument(setValueArg);
        setCommand.AddOption(secureOption);

        setCommand.SetHandler(async (key, value, secure) =>
        {
            try
            {
                var configManager = new SecureConfigurationManager();

                if (secure)
                {
                    await configManager.SetSecureAsync(key, value);
                    Console.WriteLine($"✅ Secure configuration '{key}' set");
                }
                else
                {
                    await configManager.SetAsync(key, value);
                    Console.WriteLine($"✅ Configuration '{key}' set");
                }

                System.Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to set configuration: {ex.Message}");
                System.Environment.ExitCode = 1;
            }
        }, setKeyArg, setValueArg, secureOption);

        var listCommand = new Command("list", "List all configuration values");
        listCommand.SetHandler(async () =>
        {
            try
            {
                var configManager = new SecureConfigurationManager();
                var items = await configManager.ListConfigurationAsync();

                Console.WriteLine("📋 Configuration Items:");
                Console.WriteLine("======================");

                foreach (var item in items)
                {
                    var secureIndicator = item.IsSecure ? "🔒" : "📄";
                    var value = item.IsSecure ? "[encrypted]" : item.Value ?? "[null]";
                    Console.WriteLine($"{secureIndicator} {item.Key}: {value}");
                    Console.WriteLine($"   Last modified: {item.LastModified:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine();
                }

                System.Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to list configuration: {ex.Message}");
                System.Environment.ExitCode = 1;
            }
        });

        configCommand.AddCommand(setCommand);
        configCommand.AddCommand(listCommand);

        // GitHub command for PR testing
        var githubCommand = new Command("github", "GitHub integration setup");
        var githubTokenOption = new Option<string>("--token", "GitHub token for PR testing");
        var githubRepoOption = new Option<string>("--repo", () => "eoniclabs/MethodCache", "GitHub repository");

        githubCommand.AddOption(githubTokenOption);
        githubCommand.AddOption(githubRepoOption);

        githubCommand.SetHandler(async (token, repo) =>
        {
            try
            {
                var configManager = new SecureConfigurationManager();

                if (!string.IsNullOrEmpty(token))
                {
                    await configManager.SetSecureAsync("github_token", token);
                    Console.WriteLine("✅ GitHub token stored securely");
                }

                if (!string.IsNullOrEmpty(repo))
                {
                    await configManager.SetAsync("github_repo", repo);
                    Console.WriteLine($"✅ GitHub repository set to: {repo}");
                }

                Console.WriteLine("🐙 GitHub configuration complete");
                Console.WriteLine("   You can now run PR-related integration tests");

                System.Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ GitHub setup failed: {ex.Message}");
                System.Environment.ExitCode = 1;
            }
        }, githubTokenOption, githubRepoOption);

        // Performance command
        var perfCommand = new Command("perf", "Performance testing and optimization");
        perfCommand.SetHandler(async () =>
        {
            try
            {
                var unifiedConfig = new UnifiedTestConfiguration();
                await unifiedConfig.InitializeAsync();

                var connections = await unifiedConfig.GetOptimizedConnectionStringsAsync();
                var envInfo = unifiedConfig.GetEnvironmentInfo();

                Console.WriteLine("🚀 Performance Analysis");
                Console.WriteLine("=======================");
                Console.WriteLine();

                Console.WriteLine($"Platform: {envInfo.Platform} ({envInfo.Architecture})");
                Console.WriteLine($"SQL Server: {connections.SqlServerSource}");
                Console.WriteLine($"Redis: {connections.RedisSource}");
                Console.WriteLine();

                // Estimate performance
                var dockerServices = 0;
                if (connections.SqlServerSource == "Docker container") dockerServices++;
                if (connections.RedisSource == "Docker container") dockerServices++;

                var estimate = dockerServices switch
                {
                    0 => "🚀 Excellent (~30 seconds)",
                    1 => "⚡ Good (~2 minutes)",
                    2 => "🐋 Moderate (~5 minutes first run, ~1 minute subsequent)",
                    _ => "❓ Unknown"
                };

                Console.WriteLine($"Estimated test performance: {estimate}");
                Console.WriteLine();

                // Recommendations
                if (dockerServices > 0)
                {
                    Console.WriteLine("💡 Performance Optimization Tips:");
                    Console.WriteLine("- Set up external Redis and SQL Server for fastest tests");
                    Console.WriteLine("- Use 'docker-compose -f docker-compose.dev.yml up -d' for persistent containers");

                    if (envInfo.Platform == "macOS" && envInfo.Architecture == "Arm64")
                    {
                        Console.WriteLine("- Enable Rosetta in Docker Desktop for better SQL Server performance");
                    }
                }

                System.Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Performance analysis failed: {ex.Message}");
                System.Environment.ExitCode = 1;
            }
        });

        rootCommand.AddCommand(setupCommand);
        rootCommand.AddCommand(validateCommand);
        rootCommand.AddCommand(configCommand);
        rootCommand.AddCommand(githubCommand);
        rootCommand.AddCommand(perfCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static void DisplayEnvironmentInfo(TestEnvironmentInfo envInfo)
    {
        Console.WriteLine("🔍 Environment Detection Results");
        Console.WriteLine("================================");
        Console.WriteLine();

        Console.WriteLine($"Platform: {envInfo.Platform} ({envInfo.Architecture})");
        Console.WriteLine($"Docker: {(envInfo.Docker.IsInstalled ? "✅ Installed" : "❌ Not found")} " +
                         $"{(envInfo.Docker.IsRunning ? "(Running)" : "(Not running)")}");

        if (envInfo.Docker.IsInstalled && !string.IsNullOrEmpty(envInfo.Docker.Version))
        {
            Console.WriteLine($"  Version: {envInfo.Docker.Version}");
        }

        Console.WriteLine($"Redis: {(envInfo.Redis.IsAvailable ? "✅ Available" : "❌ Not available")}");
        if (envInfo.Redis.IsAvailable)
        {
            Console.WriteLine($"  Connection: {envInfo.Redis.ConnectionString}");
            Console.WriteLine($"  Version: {envInfo.Redis.Version}");
        }

        Console.WriteLine($"SQL Server: {(envInfo.SqlServer.IsAvailable ? "✅ Available" : "❌ Not available")}");
        if (envInfo.SqlServer.IsAvailable)
        {
            Console.WriteLine($"  Connection: {envInfo.SqlServer.ConnectionString}");
            Console.WriteLine($"  Version: {envInfo.SqlServer.Version}");
        }

        Console.WriteLine();
        Console.WriteLine("📋 Setup Recommendations:");
        foreach (var instruction in envInfo.SetupInstructions)
        {
            Console.WriteLine($"  {instruction}");
        }
        Console.WriteLine();
    }

    private static void DisplayValidationResults(ConfigurationValidationResult validation)
    {
        Console.WriteLine("✅ Configuration Validation");
        Console.WriteLine("===========================");
        Console.WriteLine();

        Console.WriteLine($"SQL Server: {validation.SqlServerMessage}");
        Console.WriteLine($"Redis: {validation.RedisMessage}");
        Console.WriteLine();

        Console.WriteLine($"Overall Status: {(validation.IsValid ? "✅ Valid" : "❌ Invalid")}");
        Console.WriteLine($"Performance Estimate: {validation.PerformanceEstimate}");
        Console.WriteLine();

        if (!validation.IsValid)
        {
            Console.WriteLine("🛠️  To fix issues:");
            Console.WriteLine("   1. Run: dotnet run --project Tests/MethodCache.Tests.Infrastructure setup");
            Console.WriteLine("   2. Or use platform setup script: scripts/setup-dev-env.ps1 (Windows) or scripts/setup-dev-env.sh (macOS/Linux)");
        }
    }
}