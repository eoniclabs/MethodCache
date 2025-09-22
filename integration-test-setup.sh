#!/bin/bash

# 🧪 MethodCache L3 Integration Test Setup Script
# Based on Microsoft's guidance: https://devblogs.microsoft.com/azure-sql/development-with-sql-in-containers-on-macos/

set -e

echo "🚀 Setting up MethodCache L3 Integration Tests"
echo "============================================="

# Check if we're on macOS
if [[ "$OSTYPE" == "darwin"* ]]; then
    echo "📱 Detected macOS"

    # Check architecture
    ARCH=$(uname -m)
    if [[ "$ARCH" == "arm64" ]]; then
        echo "🍎 Detected Apple Silicon (M1/M2)"
        echo "⚡ Installing Rosetta 2 for x86/amd64 emulation..."
        softwareupdate --install-rosetta --agree-to-license || echo "Rosetta already installed"

        echo "📋 Please ensure Docker Desktop has Rosetta emulation enabled:"
        echo "   1. Open Docker Desktop"
        echo "   2. Go to Settings → Features in development"
        echo "   3. Enable 'Use Rosetta for x86/amd64 emulation'"
        echo "   4. Restart Docker Desktop"
        echo ""
    fi
fi

# Check Docker availability
echo "🐋 Checking Docker availability..."
if ! docker --version > /dev/null 2>&1; then
    echo "❌ Docker not found. Please install Docker Desktop first."
    exit 1
fi

DOCKER_VERSION=$(docker --version | grep -o '[0-9]\+\.[0-9]\+' | head -1)
echo "✅ Docker version: $DOCKER_VERSION"

# Check if Docker is running
if ! docker info > /dev/null 2>&1; then
    echo "❌ Docker is not running. Please start Docker Desktop."
    exit 1
fi

echo "✅ Docker is running"

# Offer setup options
echo ""
echo "🎯 Choose integration test setup:"
echo "1. 🚀 External SQL Server (Fastest - recommended for development)"
echo "2. 🐋 Docker SQL Server (Automatic container management)"
echo "3. 📋 Manual Docker setup (Full control)"
echo ""

read -p "Enter your choice (1-3): " choice

case $choice in
    1)
        echo ""
        echo "🚀 Setting up External SQL Server"
        echo "================================="
        echo ""
        echo "Please set up a local SQL Server instance and run:"
        echo ""
        echo "export METHODCACHE_SQLSERVER_URL=\"Server=localhost;Database=MethodCacheTests;Trusted_Connection=true;\""
        echo ""
        echo "Or if using SQL Server with authentication:"
        echo "export METHODCACHE_SQLSERVER_URL=\"Server=localhost;Database=MethodCacheTests;User Id=sa;Password=YourPassword;\""
        echo ""
        echo "Then run tests with:"
        echo "dotnet test MethodCache.Providers.SqlServer.IntegrationTests"
        ;;
    2)
        echo ""
        echo "🐋 Setting up Docker SQL Server (Automatic)"
        echo "==========================================="
        echo ""
        echo "Tests will automatically create and manage SQL Server containers."
        echo "First run will take 3-5 minutes, subsequent runs will be faster due to container reuse."
        echo ""
        echo "Run tests with:"
        echo "dotnet test MethodCache.Providers.SqlServer.IntegrationTests"
        ;;
    3)
        echo ""
        echo "📋 Manual Docker Setup"
        echo "======================"
        echo ""

        # Determine platform flag based on architecture
        PLATFORM_FLAG=""
        if [[ "$ARCH" == "arm64" ]]; then
            PLATFORM_FLAG="--platform linux/amd64"
            echo "🍎 Using platform linux/amd64 for Apple Silicon compatibility"
        fi

        echo "Creating SQL Server container..."

        # Stop and remove existing container if it exists
        docker stop sql-methodcache-test 2>/dev/null || true
        docker rm sql-methodcache-test 2>/dev/null || true

        # Start new container
        docker run --name sql-methodcache-test $PLATFORM_FLAG \
            -e "ACCEPT_EULA=Y" \
            -e "SA_PASSWORD=YourStrong@Passw0rd" \
            -e "MSSQL_PID=Developer" \
            -p 1433:1433 \
            -d mcr.microsoft.com/mssql/server:2022-latest

        echo "⏳ Waiting for SQL Server to start..."
        sleep 30

        # Test connection
        echo "🔍 Testing connection..."
        if docker exec sql-methodcache-test /opt/mssql-tools18/bin/sqlcmd \
            -S localhost -U sa -P "YourStrong@Passw0rd" -Q "SELECT 1" -C > /dev/null 2>&1; then
            echo "✅ SQL Server is ready!"

            # Create test database
            echo "📊 Creating test database..."
            docker exec sql-methodcache-test /opt/mssql-tools18/bin/sqlcmd \
                -S localhost -U sa -P "YourStrong@Passw0rd" \
                -Q "CREATE DATABASE MethodCacheTests" -C

            echo ""
            echo "🎯 Setup complete! Set environment variable and run tests:"
            echo ""
            echo "export METHODCACHE_SQLSERVER_URL=\"Server=localhost,1433;Database=MethodCacheTests;User Id=sa;Password=YourStrong@Passw0rd;\""
            echo "dotnet test MethodCache.Providers.SqlServer.IntegrationTests"
            echo ""
            echo "🛑 To stop the container later:"
            echo "docker stop sql-methodcache-test"
            echo "docker rm sql-methodcache-test"
        else
            echo "❌ Failed to connect to SQL Server. Please check Docker logs:"
            echo "docker logs sql-methodcache-test"
        fi
        ;;
    *)
        echo "❌ Invalid choice. Please run the script again."
        exit 1
        ;;
esac

echo ""
echo "🎉 Setup instructions provided!"
echo ""
echo "📚 For more information, see:"
echo "   - INTEGRATION_TEST_STRATEGY.md"
echo "   - https://devblogs.microsoft.com/azure-sql/development-with-sql-in-containers-on-macos/"