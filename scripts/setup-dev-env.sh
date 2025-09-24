#!/bin/bash

# MethodCache Development Environment Setup Script for macOS/Linux
# This script sets up the optimal development environment for MethodCache integration tests

set -e

# Configuration
INTERACTIVE=true
DOCKER_ONLY=false
EXTERNAL_ONLY=false
CONFIG_PATH=""

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --non-interactive)
            INTERACTIVE=false
            shift
            ;;
        --docker-only)
            DOCKER_ONLY=true
            shift
            ;;
        --external-only)
            EXTERNAL_ONLY=true
            shift
            ;;
        --config-path)
            CONFIG_PATH="$2"
            shift 2
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Function to print colored output
print_status() {
    echo -e "${CYAN}$1${NC}"
}

print_success() {
    echo -e "${GREEN}$1${NC}"
}

print_warning() {
    echo -e "${YELLOW}$1${NC}"
}

print_error() {
    echo -e "${RED}$1${NC}"
}

print_info() {
    echo -e "${BLUE}$1${NC}"
}

# Function to check if command exists
command_exists() {
    command -v "$1" >/dev/null 2>&1
}

# Function to check if service is running (macOS/Linux)
service_running() {
    case "$(uname)" in
        "Darwin")
            # macOS
            brew services list | grep "$1" | grep -q "started" 2>/dev/null
            ;;
        "Linux")
            # Linux
            systemctl is-active --quiet "$1" 2>/dev/null
            ;;
        *)
            return 1
            ;;
    esac
}

# Function to get platform info
get_platform() {
    case "$(uname)" in
        "Darwin")
            echo "macOS"
            ;;
        "Linux")
            echo "Linux"
            ;;
        *)
            echo "Unknown"
            ;;
    esac
}

# Function to get architecture
get_architecture() {
    uname -m
}

# Function to test SQL Server connection
test_sql_connection() {
    local conn_string="$1"

    # Try using sqlcmd if available
    if command_exists sqlcmd; then
        timeout 5 sqlcmd -S "$conn_string" -Q "SELECT 1" >/dev/null 2>&1
    else
        # Fallback: just check if we can connect to the port
        local host=$(echo "$conn_string" | cut -d',' -f1)
        local port=${conn_string##*,}
        if [[ "$port" == "$host" ]]; then
            port="1433"
        fi
        timeout 3 bash -c "</dev/tcp/$host/$port" >/dev/null 2>&1
    fi
}

# Function to test Redis connection
test_redis_connection() {
    local conn_string="$1"
    local host=${conn_string%:*}
    local port=${conn_string##*:}

    if [[ "$port" == "$host" ]]; then
        port="6379"
    fi

    timeout 3 bash -c "</dev/tcp/$host/$port" >/dev/null 2>&1
}

# Function to install package based on platform
install_package() {
    local package_name="$1"
    local display_name="$2"

    print_warning "📦 Installing $display_name..."

    case "$(uname)" in
        "Darwin")
            if command_exists brew; then
                brew install "$package_name"
                return $?
            else
                print_error "Homebrew not found. Please install it first: https://brew.sh/"
                return 1
            fi
            ;;
        "Linux")
            if command_exists apt-get; then
                sudo apt-get update && sudo apt-get install -y "$package_name"
                return $?
            elif command_exists yum; then
                sudo yum install -y "$package_name"
                return $?
            elif command_exists dnf; then
                sudo dnf install -y "$package_name"
                return $?
            else
                print_error "No supported package manager found"
                return 1
            fi
            ;;
        *)
            print_error "Unsupported platform"
            return 1
            ;;
    esac
}

# Main setup
print_status "🧪 MethodCache Development Environment Setup ($(get_platform))"
print_status "=============================================================="
echo

print_warning "🔍 Detecting current environment..."

# Platform and architecture detection
PLATFORM=$(get_platform)
ARCH=$(get_architecture)
print_info "Platform: $PLATFORM ($ARCH)"

# Check Docker
DOCKER_INSTALLED=false
DOCKER_RUNNING=false
DOCKER_VERSION=""

if command_exists docker; then
    DOCKER_INSTALLED=true
    DOCKER_VERSION=$(docker --version | sed 's/Docker version //' | sed 's/,.*//')

    if docker info >/dev/null 2>&1; then
        DOCKER_RUNNING=true
        print_success "✅ Docker installed and running (v$DOCKER_VERSION)"

        # Check for Apple Silicon specific recommendations
        if [[ "$PLATFORM" == "macOS" && "$ARCH" == "arm64" ]]; then
            if pgrep -f "com.docker.hyperkit" >/dev/null || pgrep -f "qemu" >/dev/null; then
                print_info "🍎 Apple Silicon detected - checking Rosetta configuration..."

                # Check if Rosetta is enabled (simplified check)
                if pgrep oahd >/dev/null 2>&1; then
                    print_success "  ✅ Rosetta 2 is available"
                else
                    print_warning "  ⚠️  Rosetta 2 not detected - may need to enable for SQL Server"
                    print_info "     Run: softwareupdate --install-rosetta --agree-to-license"
                fi
            fi
        fi
    else
        print_warning "⚠️  Docker installed but not running"
    fi
else
    print_error "❌ Docker not found"
fi

# Check SQL Server
SQL_AVAILABLE=false
SQL_INSTANCES=()

# Try common SQL Server connection patterns
SQL_CONNECTIONS=(
    "localhost"
    "localhost,1433"
    "127.0.0.1"
    "127.0.0.1,1433"
)

for conn in "${SQL_CONNECTIONS[@]}"; do
    if test_sql_connection "$conn"; then
        SQL_AVAILABLE=true
        SQL_INSTANCES+=("$conn")
        print_success "✅ SQL Server found: $conn"
        break
    fi
done

if [[ "$SQL_AVAILABLE" == false ]]; then
    print_error "❌ No SQL Server instances found"
fi

# Check Redis
REDIS_AVAILABLE=false
REDIS_CONNECTION=""

# Check if Redis is running via service or direct connection
if service_running redis || service_running redis-server; then
    REDIS_AVAILABLE=true
    REDIS_CONNECTION="localhost:6379"
    print_success "✅ Redis service running"

    if test_redis_connection "$REDIS_CONNECTION"; then
        print_success "  ✅ Redis connection successful"
    else
        print_warning "  ⚠️  Redis connection failed"
        REDIS_AVAILABLE=false
    fi
elif test_redis_connection "localhost:6379"; then
    REDIS_AVAILABLE=true
    REDIS_CONNECTION="localhost:6379"
    print_success "✅ Redis available at localhost:6379"
else
    print_error "❌ Redis not found or not running"
fi

echo

# Determine recommendation
RECOMMENDATION="Unknown"
ESTIMATED_TIME="Unknown"

if [[ "$SQL_AVAILABLE" == true && "$REDIS_AVAILABLE" == true ]]; then
    RECOMMENDATION="External Services (Optimal)"
    ESTIMATED_TIME="~30 seconds"
    print_success "🚀 Optimal setup detected! Using external services for fastest test execution."
elif [[ "$DOCKER_INSTALLED" == true && "$DOCKER_RUNNING" == true ]]; then
    RECOMMENDATION="Docker Containers"
    ESTIMATED_TIME="~5 minutes first run, ~1 minute subsequent"
    print_info "🐋 Docker available - will use containers for missing services."
else
    RECOMMENDATION="Setup Required"
    ESTIMATED_TIME="Setup needed"
    print_warning "⚠️  Additional setup required for optimal development experience."
fi

print_status "📊 Current Setup: $RECOMMENDATION"
print_status "⏱️  Estimated Test Time: $ESTIMATED_TIME"
echo

# Interactive setup
if [[ "$INTERACTIVE" == true && "$DOCKER_ONLY" == false && "$EXTERNAL_ONLY" == false ]]; then
    print_status "🛠️  Setup Options:"
    echo "1. 🚀 Set up external services (fastest - SQL Server + Redis locally)"
    echo "2. 🐋 Use Docker containers only (good - automatic but slower)"
    echo "3. 🔧 Manual configuration"
    echo "4. ⏭️  Skip setup (use current configuration)"
    echo

    while true; do
        read -p "Choose setup option (1-4): " choice
        case $choice in
            1)
                EXTERNAL_ONLY=true
                break
                ;;
            2)
                DOCKER_ONLY=true
                break
                ;;
            3)
                print_info "Manual configuration mode - you'll be prompted for each step"
                break
                ;;
            4)
                print_info "Skipping setup - using current configuration"
                exit 0
                ;;
            *)
                print_error "Please choose 1-4"
                ;;
        esac
    done
fi

# External Services Setup
if [[ "$EXTERNAL_ONLY" == true || ("$DOCKER_ONLY" == false && "$INTERACTIVE" == true) ]]; then
    echo
    print_status "🗄️  SQL Server Setup"
    print_status "=================="

    if [[ "$SQL_AVAILABLE" == false ]]; then
        print_warning "SQL Server not found. Installation options:"

        case "$PLATFORM" in
            "macOS")
                echo "1. SQL Server 2022 Developer Edition (Docker-based)"
                echo "2. Azure SQL Edge (lighter alternative)"
                echo "3. Skip (will use Docker)"
                ;;
            "Linux")
                echo "1. SQL Server 2022 Developer Edition"
                echo "2. Install via package manager"
                echo "3. Skip (will use Docker)"
                ;;
        esac

        if [[ "$INTERACTIVE" == true ]]; then
            while true; do
                read -p "Choose SQL Server option (1-3): " sql_choice
                case $sql_choice in
                    1)
                        case "$PLATFORM" in
                            "macOS")
                                print_warning "Setting up SQL Server via Docker..."
                                docker run -e "ACCEPT_EULA=Y" -e "SA_PASSWORD=YourStrong@Passw0rd" \
                                    -p 1433:1433 --name sqlserver-dev \
                                    --platform linux/amd64 \
                                    -d mcr.microsoft.com/mssql/server:2022-latest
                                ;;
                            "Linux")
                                print_warning "Installing SQL Server..."
                                # Add Microsoft repository and install SQL Server
                                if command_exists apt-get; then
                                    curl -sSL https://packages.microsoft.com/keys/microsoft.asc | sudo apt-key add -
                                    sudo add-apt-repository "$(curl -sSL https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/mssql-server-2022.list)"
                                    sudo apt-get update
                                    sudo apt-get install -y mssql-server
                                    sudo /opt/mssql/bin/mssql-conf setup
                                fi
                                ;;
                        esac
                        break
                        ;;
                    2)
                        case "$PLATFORM" in
                            "macOS")
                                print_warning "Installing Azure SQL Edge..."
                                docker run -e "ACCEPT_EULA=Y" -e "SA_PASSWORD=YourStrong@Passw0rd" \
                                    -p 1433:1433 --name sqledge-dev \
                                    -d mcr.microsoft.com/azure-sql-edge:latest
                                ;;
                            "Linux")
                                install_package "mssql-server" "SQL Server"
                                ;;
                        esac
                        break
                        ;;
                    3)
                        print_info "Skipping SQL Server installation - will use Docker"
                        break
                        ;;
                    *)
                        print_error "Please choose 1-3"
                        ;;
                esac
            done
        fi
    fi

    echo
    print_status "🔄 Redis Setup"
    print_status "==============="

    if [[ "$REDIS_AVAILABLE" == false ]]; then
        print_warning "Redis not found. Installation options:"
        echo "1. Install Redis via package manager"
        echo "2. Skip (will use Docker)"

        if [[ "$INTERACTIVE" == true ]]; then
            while true; do
                read -p "Choose Redis option (1-2): " redis_choice
                case $redis_choice in
                    1)
                        case "$PLATFORM" in
                            "macOS")
                                install_package "redis" "Redis"
                                if command_exists brew; then
                                    brew services start redis
                                fi
                                ;;
                            "Linux")
                                install_package "redis-server" "Redis"
                                if command_exists systemctl; then
                                    sudo systemctl enable redis-server
                                    sudo systemctl start redis-server
                                fi
                                ;;
                        esac
                        break
                        ;;
                    2)
                        print_info "Skipping Redis installation - will use Docker"
                        break
                        ;;
                    *)
                        print_error "Please choose 1-2"
                        ;;
                esac
            done
        fi
    fi
fi

# Docker Setup
if [[ "$DOCKER_ONLY" == true || ("$EXTERNAL_ONLY" == false && "$DOCKER_RUNNING" == false) ]]; then
    echo
    print_status "🐋 Docker Setup"
    print_status "==============="

    if [[ "$DOCKER_INSTALLED" == false ]]; then
        print_warning "Docker not found. Installation instructions:"

        case "$PLATFORM" in
            "macOS")
                print_info "Download and install Docker Desktop for Mac:"
                print_info "  https://desktop.docker.com/mac/main/$(get_architecture)/Docker.dmg"

                if command_exists brew; then
                    print_info "Or install via Homebrew: brew install --cask docker"
                fi
                ;;
            "Linux")
                print_info "Install Docker via package manager:"
                print_info "  curl -fsSL https://get.docker.com -o get-docker.sh"
                print_info "  sudo sh get-docker.sh"
                print_info "  sudo usermod -aG docker \$USER"
                ;;
        esac

        if [[ "$INTERACTIVE" == true ]]; then
            read -p "Press Enter after installing Docker..."
        fi
    fi

    if [[ "$DOCKER_RUNNING" == false ]]; then
        print_warning "Docker is not running. Please start Docker."

        case "$PLATFORM" in
            "macOS")
                print_info "Start Docker Desktop from Applications folder"
                ;;
            "Linux")
                print_info "Start Docker service: sudo systemctl start docker"
                ;;
        esac

        if [[ "$INTERACTIVE" == true ]]; then
            read -p "Press Enter after starting Docker..."
        fi
    fi
fi

# Configuration Setup
echo
print_status "⚙️  Configuration Setup"
print_status "======================"

# Build and run configuration tool
CONFIG_TOOL_DIR="$(dirname "$0")/../Tests/MethodCache.Tests.Infrastructure"
CONFIG_TOOL="$CONFIG_TOOL_DIR/bin/Release/net9.0/MethodCache.Tests.Infrastructure"

if [[ -f "$CONFIG_TOOL" ]]; then
    print_warning "Running configuration tool..."

    ARGS=("setup")
    if [[ "$INTERACTIVE" == false ]]; then
        ARGS+=("--non-interactive")
    fi
    if [[ -n "$CONFIG_PATH" ]]; then
        ARGS+=("--config-path" "$CONFIG_PATH")
    fi

    "$CONFIG_TOOL" "${ARGS[@]}"
else
    print_warning "Configuration tool not found. Building..."

    if command_exists dotnet; then
        (
            cd "$CONFIG_TOOL_DIR"
            dotnet build -c Release

            if [[ -f "$CONFIG_TOOL" ]]; then
                "$CONFIG_TOOL" setup
            else
                print_warning "⚠️  Could not build configuration tool. Manual setup required."
            fi
        )
    else
        print_error "❌ .NET SDK not found. Please install .NET 9.0 SDK"
    fi
fi

# Environment Variables
echo
print_status "🌍 Environment Variables"
print_status "========================"

# Create environment setup file
ENV_FILE="$HOME/.methodcache-env"

{
    echo "# MethodCache Test Environment Variables"
    echo "# Source this file: source ~/.methodcache-env"
    echo

    # SQL Server
    if [[ "$SQL_AVAILABLE" == true ]]; then
        SQL_CONN="Server=${SQL_INSTANCES[0]};Database=MethodCacheTests;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=true;"
        echo "export METHODCACHE_SQLSERVER_URL=\"$SQL_CONN\""
        print_success "METHODCACHE_SQLSERVER_URL=$SQL_CONN"
    fi

    # Redis
    if [[ "$REDIS_AVAILABLE" == true ]]; then
        echo "export METHODCACHE_REDIS_URL=\"$REDIS_CONNECTION\""
        print_success "METHODCACHE_REDIS_URL=$REDIS_CONNECTION"
    fi

    echo
    echo "# Auto-generated on $(date)"
} > "$ENV_FILE"

print_info "Environment variables saved to: $ENV_FILE"
print_info "To use in your shell: source $ENV_FILE"

# Update shell profile
SHELL_PROFILE=""
case "$SHELL" in
    */bash)
        SHELL_PROFILE="$HOME/.bashrc"
        ;;
    */zsh)
        SHELL_PROFILE="$HOME/.zshrc"
        ;;
    */fish)
        SHELL_PROFILE="$HOME/.config/fish/config.fish"
        ;;
esac

if [[ -n "$SHELL_PROFILE" && "$INTERACTIVE" == true ]]; then
    read -p "Add environment variables to $SHELL_PROFILE? (y/n): " add_to_profile
    if [[ "$add_to_profile" == "y" || "$add_to_profile" == "Y" ]]; then
        echo "" >> "$SHELL_PROFILE"
        echo "# MethodCache test environment" >> "$SHELL_PROFILE"
        echo "source $ENV_FILE" >> "$SHELL_PROFILE"
        print_success "✅ Added to $SHELL_PROFILE"
    fi
fi

# Final Summary
echo
print_success "🎉 Setup Complete!"
print_success "=================="

# Re-evaluate setup after changes
FINAL_SQL_AVAILABLE="$SQL_AVAILABLE"
FINAL_REDIS_AVAILABLE="$REDIS_AVAILABLE"
FINAL_DOCKER_AVAILABLE="$DOCKER_INSTALLED"

if [[ "$FINAL_SQL_AVAILABLE" == true && "$FINAL_REDIS_AVAILABLE" == true ]]; then
    print_success "✅ Optimal setup: External SQL Server + Redis"
    print_success "⚡ Estimated test time: ~30 seconds"
elif [[ "$FINAL_DOCKER_AVAILABLE" == true ]]; then
    print_success "✅ Good setup: Docker containers available"
    print_success "🐋 Estimated test time: ~5 minutes first run, ~1 minute subsequent"
else
    print_warning "⚠️  Partial setup: Additional configuration may be needed"
fi

echo
print_status "🧪 To run integration tests:"
print_info "   dotnet test MethodCache.Providers.SqlServer.IntegrationTests"
print_info "   dotnet test MethodCache.Providers.Redis.IntegrationTests"
echo
print_status "📚 For more information, see:"
print_info "   - INTEGRATION_TEST_STRATEGY.md"
print_info "   - Tests/MethodCache.Tests.Infrastructure/README.md"