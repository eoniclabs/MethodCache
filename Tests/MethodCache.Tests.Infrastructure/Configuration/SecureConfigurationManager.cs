using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MethodCache.Tests.Infrastructure.Configuration;

/// <summary>
/// Manages secure storage and retrieval of test configuration including sensitive data like connection strings and API keys.
/// Provides cross-platform, cross-machine secure credential management for development environments.
/// </summary>
public class SecureConfigurationManager
{
    private readonly string _configDirectory;
    private readonly string _encryptionKey;

    public SecureConfigurationManager(string? configDirectory = null)
    {
        _configDirectory = configDirectory ?? GetDefaultConfigDirectory();
        _encryptionKey = GetOrCreateMachineKey();
        EnsureConfigDirectoryExists();
    }

    /// <summary>
    /// Stores a secure configuration value (encrypted on disk)
    /// </summary>
    public async Task SetSecureAsync(string key, string value)
    {
        var encryptedValue = EncryptString(value, _encryptionKey);
        var configPath = Path.Combine(_configDirectory, $"{key}.secure");
        await File.WriteAllTextAsync(configPath, encryptedValue);

        // Set restrictive permissions
        SetSecureFilePermissions(configPath);
    }

    /// <summary>
    /// Retrieves a secure configuration value (decrypted from disk)
    /// </summary>
    public async Task<string?> GetSecureAsync(string key)
    {
        var configPath = Path.Combine(_configDirectory, $"{key}.secure");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var encryptedValue = await File.ReadAllTextAsync(configPath);
            return DecryptString(encryptedValue, _encryptionKey);
        }
        catch (Exception)
        {
            // If decryption fails, the key might have changed - return null
            return null;
        }
    }

    /// <summary>
    /// Stores a plain configuration value (not encrypted, for non-sensitive data)
    /// </summary>
    public async Task SetAsync(string key, string value)
    {
        var configPath = Path.Combine(_configDirectory, $"{key}.json");
        var config = new { value, timestamp = DateTimeOffset.UtcNow };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, json);
    }

    /// <summary>
    /// Retrieves a plain configuration value
    /// </summary>
    public async Task<string?> GetAsync(string key)
    {
        var configPath = Path.Combine(_configDirectory, $"{key}.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            var config = JsonSerializer.Deserialize<JsonElement>(json);
            return config.GetProperty("value").GetString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets a configuration value with fallback to environment variables
    /// </summary>
    public async Task<string?> GetWithFallbackAsync(string key, params string[] environmentVariables)
    {
        // Try secure config first
        var value = await GetSecureAsync(key);
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Try plain config
        value = await GetAsync(key);
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Try environment variables
        foreach (var envVar in environmentVariables)
        {
            value = System.Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Sets up common test configuration values
    /// </summary>
    public async Task InitializeDefaultsAsync()
    {
        await SetupDatabaseConfigAsync();
        await SetupRedisConfigAsync();
        await SetupGitHubConfigAsync();
    }

    /// <summary>
    /// Gets the complete test configuration
    /// </summary>
    public async Task<TestConfiguration> GetTestConfigurationAsync()
    {
        return new TestConfiguration
        {
            SqlServer = new DatabaseConfig
            {
                ConnectionString = await GetWithFallbackAsync("sqlserver_connection",
                    "METHODCACHE_SQLSERVER_URL", "SQLSERVER_URL"),
                TestDatabaseName = await GetAsync("sqlserver_test_db") ?? "MethodCacheTests"
            },
            Redis = new RedisConfig
            {
                ConnectionString = await GetWithFallbackAsync("redis_connection",
                    "METHODCACHE_REDIS_URL", "REDIS_URL") ?? "localhost:6379",
                TestKeyPrefix = await GetAsync("redis_test_prefix") ?? $"test:{Guid.NewGuid():N}:"
            },
            GitHub = new GitHubConfig
            {
                Token = await GetSecureAsync("github_token"),
                Repository = await GetAsync("github_repo") ?? "eoniclabs/MethodCache"
            },
            Docker = new DockerConfig
            {
                UseContainers = bool.Parse(await GetAsync("docker_use_containers") ?? "true"),
                ContainerTimeout = TimeSpan.Parse(await GetAsync("docker_timeout") ?? "00:05:00"),
                ReuseContainers = bool.Parse(await GetAsync("docker_reuse") ?? "true")
            }
        };
    }

    /// <summary>
    /// Lists all available configuration keys
    /// </summary>
    public async Task<List<ConfigurationItem>> ListConfigurationAsync()
    {
        var items = new List<ConfigurationItem>();

        if (Directory.Exists(_configDirectory))
        {
            foreach (var file in Directory.GetFiles(_configDirectory))
            {
                var fileName = Path.GetFileName(file);
                var isSecure = fileName.EndsWith(".secure");
                var key = fileName.Replace(".secure", "").Replace(".json", "");

                var item = new ConfigurationItem
                {
                    Key = key,
                    IsSecure = isSecure,
                    LastModified = File.GetLastWriteTime(file),
                    HasValue = true
                };

                if (!isSecure)
                {
                    item.Value = await GetAsync(key);
                }

                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>
    /// Removes a configuration value
    /// </summary>
    public async Task RemoveAsync(string key)
    {
        var secureFile = Path.Combine(_configDirectory, $"{key}.secure");
        var plainFile = Path.Combine(_configDirectory, $"{key}.json");

        if (File.Exists(secureFile))
        {
            File.Delete(secureFile);
        }

        if (File.Exists(plainFile))
        {
            File.Delete(plainFile);
        }
    }

    private async Task SetupDatabaseConfigAsync()
    {
        // Set up SQL Server configuration if not already present
        var sqlConnection = await GetWithFallbackAsync("sqlserver_connection",
            "METHODCACHE_SQLSERVER_URL", "SQLSERVER_URL");

        if (string.IsNullOrEmpty(sqlConnection))
        {
            // Provide platform-specific defaults
            var defaultConnection = System.Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => "Server=.;Database=master;Trusted_Connection=true;",
                _ => "Server=localhost,1433;Database=master;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=true;"
            };

            await SetAsync("sqlserver_connection_template", defaultConnection);
        }
    }

    private async Task SetupRedisConfigAsync()
    {
        var redisConnection = await GetWithFallbackAsync("redis_connection",
            "METHODCACHE_REDIS_URL", "REDIS_URL");

        if (string.IsNullOrEmpty(redisConnection))
        {
            await SetAsync("redis_connection", "localhost:6379");
        }
    }

    private async Task SetupGitHubConfigAsync()
    {
        var githubToken = await GetSecureAsync("github_token");
        if (string.IsNullOrEmpty(githubToken))
        {
            // Create a placeholder for the user to fill in
            await SetAsync("github_setup_instructions",
                "Run: dotnet run --project Tests/MethodCache.Tests.Infrastructure setup-github --token YOUR_TOKEN");
        }
    }

    private static string GetDefaultConfigDirectory()
    {
        var homeDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDirectory, ".methodcache", "test-config");
    }

    private string GetOrCreateMachineKey()
    {
        var keyPath = Path.Combine(_configDirectory, ".machine-key");

        if (File.Exists(keyPath))
        {
            try
            {
                return File.ReadAllText(keyPath);
            }
            catch
            {
                // If we can't read the key, create a new one
            }
        }

        // Generate a new machine-specific key
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(keyPath, key);
        SetSecureFilePermissions(keyPath);
        return key;
    }

    private void EnsureConfigDirectoryExists()
    {
        if (!Directory.Exists(_configDirectory))
        {
            Directory.CreateDirectory(_configDirectory);
            SetSecureDirectoryPermissions(_configDirectory);
        }
    }

    private static void SetSecureFilePermissions(string filePath)
    {
        try
        {
            if (System.Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // Windows: Remove access for everyone except current user
                var fileInfo = new FileInfo(filePath);
                var fileSecurity = fileInfo.GetAccessControl();
                fileSecurity.SetAccessRuleProtection(true, false);
                fileInfo.SetAccessControl(fileSecurity);
            }
            else
            {
                // Unix-like: Set permissions to 600 (owner read/write only)
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"600 \"{filePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                process.WaitForExit();
            }
        }
        catch
        {
            // Ignore permission setting errors
        }
    }

    private static void SetSecureDirectoryPermissions(string directoryPath)
    {
        try
        {
            if (System.Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                // Unix-like: Set permissions to 700 (owner read/write/execute only)
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"700 \"{directoryPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                process.WaitForExit();
            }
        }
        catch
        {
            // Ignore permission setting errors
        }
    }

    private static string EncryptString(string plainText, string key)
    {
        using var aes = Aes.Create();
        var keyBytes = Convert.FromBase64String(key);
        aes.Key = keyBytes;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Combine IV and encrypted data
        var combined = new byte[aes.IV.Length + encryptedBytes.Length];
        Array.Copy(aes.IV, 0, combined, 0, aes.IV.Length);
        Array.Copy(encryptedBytes, 0, combined, aes.IV.Length, encryptedBytes.Length);

        return Convert.ToBase64String(combined);
    }

    private static string DecryptString(string encryptedText, string key)
    {
        var combined = Convert.FromBase64String(encryptedText);

        using var aes = Aes.Create();
        var keyBytes = Convert.FromBase64String(key);
        aes.Key = keyBytes;

        // Extract IV and encrypted data
        var iv = new byte[16];
        var encryptedBytes = new byte[combined.Length - 16];
        Array.Copy(combined, 0, iv, 0, 16);
        Array.Copy(combined, 16, encryptedBytes, 0, encryptedBytes.Length);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
        return Encoding.UTF8.GetString(decryptedBytes);
    }
}

public class TestConfiguration
{
    public DatabaseConfig SqlServer { get; set; } = new();
    public RedisConfig Redis { get; set; } = new();
    public GitHubConfig GitHub { get; set; } = new();
    public DockerConfig Docker { get; set; } = new();
}

public class DatabaseConfig
{
    public string? ConnectionString { get; set; }
    public string TestDatabaseName { get; set; } = "MethodCacheTests";
}

public class RedisConfig
{
    public string ConnectionString { get; set; } = "localhost:6379";
    public string TestKeyPrefix { get; set; } = string.Empty;
}

public class GitHubConfig
{
    public string? Token { get; set; }
    public string Repository { get; set; } = "eoniclabs/MethodCache";
}

public class DockerConfig
{
    public bool UseContainers { get; set; } = true;
    public TimeSpan ContainerTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public bool ReuseContainers { get; set; } = true;
}

public class ConfigurationItem
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public bool IsSecure { get; set; }
    public DateTime LastModified { get; set; }
    public bool HasValue { get; set; }
}