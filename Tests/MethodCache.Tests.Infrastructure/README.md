# MethodCache Test Infrastructure

This project provides comprehensive tooling for managing MethodCache test environments across different development machines and platforms.

## 🚀 Quick Start

### Automated Setup (Recommended)

**Windows:**
```powershell
# Run the automated setup script
.\scripts\setup-dev-env.ps1

# Or use the configuration tool directly
dotnet run --project Tests\MethodCache.Tests.Infrastructure setup
```

**macOS/Linux:**
```bash
# Run the automated setup script
./scripts/setup-dev-env.sh

# Or use the configuration tool directly
dotnet run --project Tests/MethodCache.Tests.Infrastructure setup
```

### Manual Setup

1. **Build the configuration tool:**
   ```bash
   dotnet build Tests/MethodCache.Tests.Infrastructure
   ```

2. **Validate your environment:**
   ```bash
   dotnet run --project Tests/MethodCache.Tests.Infrastructure validate
   ```

3. **Set up configuration:**
   ```bash
   dotnet run --project Tests/MethodCache.Tests.Infrastructure setup --interactive
   ```

## 🛠 Features

### Environment Detection
- Automatic platform and architecture detection
- Docker availability and configuration checking
- External service discovery (Redis, SQL Server)
- Performance estimation based on setup

### Secure Configuration Management
- Encrypted storage for sensitive data (API keys, passwords)
- Plain storage for non-sensitive configuration
- Cross-platform file permissions
- Environment variable fallbacks

### Platform-Specific Setup
- Windows: PowerShell script with Chocolatey integration
- macOS: Bash script with Homebrew integration
- Linux: Bash script with package manager detection
- Apple Silicon: Rosetta compatibility checking

### IDE Integration
- VSCode tasks for common operations
- Launch configurations for debugging
- Settings optimized for .NET development
- Docker Compose for development services

## 📋 Commands

### Setup Commands
```bash
# Interactive setup wizard
dotnet run --project Tests/MethodCache.Tests.Infrastructure setup

# Non-interactive setup
dotnet run --project Tests/MethodCache.Tests.Infrastructure setup --non-interactive

# Custom configuration directory
dotnet run --project Tests/MethodCache.Tests.Infrastructure setup --config-path /custom/path
```

### Validation Commands
```bash
# Validate current environment
dotnet run --project Tests/MethodCache.Tests.Infrastructure validate

# Validate only (no changes)
dotnet run --project Tests/MethodCache.Tests.Infrastructure setup --validate-only
```

### Configuration Management
```bash
# Set plain configuration
dotnet run --project Tests/MethodCache.Tests.Infrastructure config set redis_connection "localhost:6379"

# Set secure configuration
dotnet run --project Tests/MethodCache.Tests.Infrastructure config set github_token "ghp_token" --secure

# List all configuration
dotnet run --project Tests/MethodCache.Tests.Infrastructure config list
```

### GitHub Integration
```bash
# Setup GitHub for PR testing
dotnet run --project Tests/MethodCache.Tests.Infrastructure github --token "ghp_token" --repo "eoniclabs/MethodCache"
```

### Performance Analysis
```bash
# Analyze current setup performance
dotnet run --project Tests/MethodCache.Tests.Infrastructure perf
```

## 🔧 Configuration Storage

Configuration is stored securely in:
- **Windows:** `%USERPROFILE%\.methodcache\test-config\`
- **macOS/Linux:** `~/.methodcache/test-config/`

### File Types
- `*.secure` - Encrypted files for sensitive data
- `*.json` - Plain files for non-sensitive configuration
- `.machine-key` - Encryption key (automatically generated)



## 🔍 Environment Variables

The tool recognizes these environment variables:

### SQL Server
- `METHODCACHE_SQLSERVER_URL`
- `SQLSERVER_URL`

### Redis
- `METHODCACHE_REDIS_URL`
- `REDIS_URL`

### Development Mode
- `METHODCACHE_DEV_MODE` - Enables development-specific features

## 🎯 IDE Integration

### VSCode
The `.vscode` directory contains:
- **tasks.json** - Build, test, and environment tasks
- **launch.json** - Debug configurations
- **settings.json** - Optimized settings for .NET development

#### Available Tasks
- `Setup Development Environment` - Run platform setup script
- `Check Test Environment` - Validate configuration
- `Run All Integration Tests` - Execute integration test suite
- `Configure Test Environment` - Interactive configuration

#### Debug Configurations
- `Setup Test Environment` - Debug the configuration tool
- `Debug SQL Server Integration Test` - Debug specific SQL Server tests
- `Debug Redis Integration Test` - Debug specific Redis tests

### Other IDEs
The configuration system works with any IDE that supports .NET development. Environment variables are set automatically when using the setup scripts.

## 🚀 Performance Optimization

### Optimal Setup (Fastest)
- External Redis server
- External SQL Server instance
- **Estimated time:** ~30 seconds

### Platform-Specific Optimizations

#### Apple Silicon (M1/M2)
- Ensure Rosetta 2 is enabled: `softwareupdate --install-rosetta --agree-to-license`
- Enable Rosetta emulation in Docker Desktop
- Use `--platform linux/amd64` for SQL Server containers

#### Windows
- Use SQL Server Express or Developer Edition for best performance
- Consider Windows Subsystem for Linux (WSL2) for Docker

#### Linux
- Native Docker performance
- Use system package managers for Redis and SQL Server

## 🔒 Security

### Credential Management
- All sensitive data is encrypted using AES-256
- Machine-specific encryption keys
- Secure file permissions (600/700 on Unix, ACL on Windows)
- No credentials stored in plain text

### Best Practices
- Rotate API tokens regularly
- Use environment-specific configurations
- Don't commit configuration files to version control
- Use the secure storage for any sensitive data

## 🐛 Troubleshooting

### Common Issues

#### Docker Not Available
```
❌ Docker not available for SQL Server integration tests
```
**Solution:** Install Docker Desktop and ensure it's running

#### Permission Denied
```
❌ Permission denied accessing configuration directory
```
**Solution:** Check file permissions or run setup as administrator/sudo

#### Connection Failed
```
❌ Redis connection failed: Connection refused
```
**Solution:** Ensure Redis is running or use Docker setup

### Debug Mode
Enable verbose logging:
```bash
export METHODCACHE_DEBUG=true
dotnet run --project Tests/MethodCache.Tests.Infrastructure validate
```

### Reset Configuration
```bash
# Remove all configuration
rm -rf ~/.methodcache/test-config  # macOS/Linux
rmdir /s %USERPROFILE%\.methodcache\test-config  # Windows
```

## 📚 Additional Resources

- [Integration Test Strategy](../../INTEGRATION_TEST_STRATEGY.md)
- [Platform Setup Scripts](../../scripts/)
- [VSCode Configuration](../../.vscode/)

## 🤝 Contributing

When adding new configuration options:

1. Add to `TestConfiguration` class
2. Update setup wizard in `UnifiedTestConfiguration`
3. Add command-line options in `Program.cs`
4. Update this README

For environment detection improvements:
1. Update `TestEnvironmentDetector`
2. Add platform-specific logic
3. Update setup scripts
4. Test across platforms