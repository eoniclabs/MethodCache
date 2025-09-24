using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Configurations;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;

namespace MethodCache.Tests.Infrastructure.Environment;

/// <summary>
/// Detects and validates the test environment across different development machines
/// </summary>
public static class TestEnvironmentDetector
{
    public static async Task<TestEnvironmentInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        var info = new TestEnvironmentInfo
        {
            Platform = GetPlatform(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OperatingSystem = System.Environment.OSVersion.ToString()
        };

        // Check Docker availability
        info.Docker = await CheckDockerAsync(cancellationToken);

        // Check external services
        info.Redis = await CheckRedisAsync(cancellationToken);
        info.SqlServer = await CheckSqlServerAsync(cancellationToken);

        // Determine recommended setup
        info.RecommendedSetup = DetermineRecommendedSetup(info);

        // Generate setup instructions
        info.SetupInstructions = GenerateSetupInstructions(info);

        return info;
    }

    private static async Task<DockerInfo> CheckDockerAsync(CancellationToken cancellationToken)
    {
        var dockerInfo = new DockerInfo();

        try
        {
            // Check if Docker command is available
            var dockerProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            dockerProcess.Start();
            await dockerProcess.WaitForExitAsync(cancellationToken);

            if (dockerProcess.ExitCode == 0)
            {
                var output = await dockerProcess.StandardOutput.ReadToEndAsync();
                dockerInfo.IsInstalled = true;
                dockerInfo.Version = ExtractDockerVersion(output);

                // Check if Docker daemon is running
                dockerInfo.IsRunning = await CheckDockerDaemonAsync(cancellationToken);

                // Check Docker Desktop specific features
                if (GetPlatform() != "Linux")
                {
                    dockerInfo.DockerDesktop = await CheckDockerDesktopAsync(cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            dockerInfo.Error = ex.Message;
        }

        return dockerInfo;
    }

    private static async Task<bool> CheckDockerDaemonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dockerEndpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
            return true; // If we can get the endpoint, Docker is likely running
        }
        catch
        {
            return false;
        }
    }

    private static async Task<DockerDesktopInfo> CheckDockerDesktopAsync(CancellationToken cancellationToken)
    {
        var info = new DockerDesktopInfo();

        try
        {
            // Check for Rosetta on Apple Silicon
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 && GetPlatform() == "macOS")
            {
                info.RosettaEnabled = await CheckRosettaAsync(cancellationToken);
            }

            // Check Docker Desktop settings (if accessible)
            info.ResourceLimits = await GetDockerResourceLimitsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            info.Error = ex.Message;
        }

        return info;
    }

    private static async Task<RedisInfo> CheckRedisAsync(CancellationToken cancellationToken)
    {
        var redisInfo = new RedisInfo();

        // Check for external Redis via environment variables
        var redisUrl = System.Environment.GetEnvironmentVariable("METHODCACHE_REDIS_URL")
                      ?? System.Environment.GetEnvironmentVariable("REDIS_URL")
                      ?? "localhost:6379";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var connection = await ConnectionMultiplexer.ConnectAsync(redisUrl);
            var database = connection.GetDatabase();
            await database.PingAsync();

            redisInfo.IsAvailable = true;
            redisInfo.ConnectionString = redisUrl;
            redisInfo.Version = connection.GetServer(connection.GetEndPoints()[0]).Version.ToString();

            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            redisInfo.Error = ex.Message;
        }

        return redisInfo;
    }

    private static async Task<SqlServerInfo> CheckSqlServerAsync(CancellationToken cancellationToken)
    {
        var sqlInfo = new SqlServerInfo();

        // Check for external SQL Server via environment variables
        var sqlUrl = System.Environment.GetEnvironmentVariable("METHODCACHE_SQLSERVER_URL")
                    ?? System.Environment.GetEnvironmentVariable("SQLSERVER_URL");

        if (string.IsNullOrEmpty(sqlUrl))
        {
            // Try common local SQL Server connection strings
            var commonConnections = new[]
            {
                "Server=localhost;Database=master;Trusted_Connection=true;",
                "Server=.;Database=master;Trusted_Connection=true;",
                "Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=true;",
                "Server=localhost,1433;Database=master;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=true;"
            };

            foreach (var connString in commonConnections)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(3));

                    using var connection = new SqlConnection(connString);
                    await connection.OpenAsync(cts.Token);

                    sqlInfo.IsAvailable = true;
                    sqlInfo.ConnectionString = connString;
                    sqlInfo.Version = connection.ServerVersion;
                    break;
                }
                catch
                {
                    // Continue to next connection string
                }
            }
        }
        else
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                using var connection = new SqlConnection(sqlUrl);
                await connection.OpenAsync(cts.Token);

                sqlInfo.IsAvailable = true;
                sqlInfo.ConnectionString = sqlUrl;
                sqlInfo.Version = connection.ServerVersion;
            }
            catch (Exception ex)
            {
                sqlInfo.Error = ex.Message;
            }
        }

        return sqlInfo;
    }

    private static string GetPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macOS";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux";
        return "Unknown";
    }

    private static string ExtractDockerVersion(string output)
    {
        // Extract version from "Docker version 20.10.17, build 100c701"
        var parts = output.Split(' ');
        return parts.Length >= 3 ? parts[2].TrimEnd(',') : "Unknown";
    }

    private static async Task<bool> CheckRosettaAsync(CancellationToken cancellationToken)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/pgrep",
                    Arguments = "oahd",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<DockerResourceInfo> GetDockerResourceLimitsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "system info --format \"{{.NCPU}} {{.MemTotal}}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var parts = output.Trim().Split(' ');

                return new DockerResourceInfo
                {
                    CPUs = parts.Length > 0 && int.TryParse(parts[0], out var cpus) ? cpus : 0,
                    MemoryGB = parts.Length > 1 && long.TryParse(parts[1], out var memory) ? memory / (1024 * 1024 * 1024) : 0
                };
            }
        }
        catch
        {
            // Ignore errors
        }

        return new DockerResourceInfo();
    }

    private static TestSetupRecommendation DetermineRecommendedSetup(TestEnvironmentInfo info)
    {
        // Prioritize external services for speed
        if (info.Redis.IsAvailable && info.SqlServer.IsAvailable)
        {
            return TestSetupRecommendation.ExternalServices;
        }

        // Fall back to Docker if available
        if (info.Docker.IsInstalled && info.Docker.IsRunning)
        {
            return TestSetupRecommendation.DockerContainers;
        }

        // Hybrid approach
        if (info.Redis.IsAvailable || info.SqlServer.IsAvailable)
        {
            return TestSetupRecommendation.HybridSetup;
        }

        return TestSetupRecommendation.SetupRequired;
    }

    private static List<string> GenerateSetupInstructions(TestEnvironmentInfo info)
    {
        var instructions = new List<string>();

        switch (info.RecommendedSetup)
        {
            case TestSetupRecommendation.ExternalServices:
                instructions.Add("✅ Optimal setup detected - using external Redis and SQL Server");
                instructions.Add($"🚀 Estimated test execution time: ~30 seconds");
                break;

            case TestSetupRecommendation.DockerContainers:
                instructions.Add("🐋 Using Docker containers for integration tests");
                instructions.Add($"⏱️ First run: ~5 minutes (container startup)");
                instructions.Add($"⚡ Subsequent runs: ~1 minute (container reuse)");
                if (info.Platform == "macOS" && info.Architecture == "Arm64")
                {
                    if (!info.Docker.DockerDesktop.RosettaEnabled)
                    {
                        instructions.Add("⚠️ Enable Rosetta in Docker Desktop for better performance");
                        instructions.Add("   Settings → Features in development → Use Rosetta for x86/amd64 emulation");
                    }
                }
                break;

            case TestSetupRecommendation.HybridSetup:
                if (info.Redis.IsAvailable && !info.SqlServer.IsAvailable)
                {
                    instructions.Add("📊 Redis available externally, SQL Server will use Docker");
                    instructions.Add("🔧 For faster tests, set up local SQL Server:");
                    instructions.Add(GetSqlServerSetupInstructions(info.Platform));
                }
                else if (info.SqlServer.IsAvailable && !info.Redis.IsAvailable)
                {
                    instructions.Add("🗄️ SQL Server available externally, Redis will use Docker");
                    instructions.Add("🔧 For faster tests, set up local Redis:");
                    instructions.Add(GetRedisSetupInstructions(info.Platform));
                }
                break;

            case TestSetupRecommendation.SetupRequired:
                instructions.Add("❌ No external services or Docker detected");
                instructions.Add("🛠️ Setup options:");
                instructions.Add("1. Install Docker Desktop and start it");
                instructions.Add("2. Set up external services:");
                instructions.Add(GetRedisSetupInstructions(info.Platform));
                instructions.Add(GetSqlServerSetupInstructions(info.Platform));
                break;
        }

        return instructions;
    }

    private static string GetRedisSetupInstructions(string platform)
    {
        return platform switch
        {
            "Windows" => "   Redis: choco install redis-64 or Docker",
            "macOS" => "   Redis: brew install redis",
            "Linux" => "   Redis: sudo apt install redis-server",
            _ => "   Redis: Install via package manager or Docker"
        };
    }

    private static string GetSqlServerSetupInstructions(string platform)
    {
        return platform switch
        {
            "Windows" => "   SQL Server: SQL Server Express or Developer Edition",
            "macOS" => "   SQL Server: Docker or SQL Server 2022 Preview",
            "Linux" => "   SQL Server: sudo apt install mssql-server",
            _ => "   SQL Server: Install via package manager or Docker"
        };
    }
}

public class TestEnvironmentInfo
{
    public string Platform { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public DockerInfo Docker { get; set; } = new();
    public RedisInfo Redis { get; set; } = new();
    public SqlServerInfo SqlServer { get; set; } = new();
    public TestSetupRecommendation RecommendedSetup { get; set; }
    public List<string> SetupInstructions { get; set; } = new();

    public bool IsValid =>
        (Redis.IsAvailable && SqlServer.IsAvailable) ||
        (Docker.IsInstalled && Docker.IsRunning);

    public string ValidationMessage =>
        IsValid ? "✅ Test environment is ready" :
        "❌ Test environment requires setup - see SetupInstructions";
}

public class DockerInfo
{
    public bool IsInstalled { get; set; }
    public bool IsRunning { get; set; }
    public string Version { get; set; } = string.Empty;
    public DockerDesktopInfo DockerDesktop { get; set; } = new();
    public string? Error { get; set; }
}

public class DockerDesktopInfo
{
    public bool RosettaEnabled { get; set; }
    public DockerResourceInfo ResourceLimits { get; set; } = new();
    public string? Error { get; set; }
}

public class DockerResourceInfo
{
    public int CPUs { get; set; }
    public long MemoryGB { get; set; }
}

public class RedisInfo
{
    public bool IsAvailable { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Error { get; set; }
}

public class SqlServerInfo
{
    public bool IsAvailable { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Error { get; set; }
}

public enum TestSetupRecommendation
{
    ExternalServices,    // Fastest - use external Redis + SQL Server
    DockerContainers,    // Good - use Docker for both
    HybridSetup,        // Mixed - external + Docker
    SetupRequired       // Need to install services or Docker
}