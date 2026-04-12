#!/bin/bash
# Automated Android SDK setup for CI/CD environments
# This script downloads and configures the Android SDK with all required components

set -e

# Configuration
ANDROID_SDK_ROOT=${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}
CMDLINE_TOOLS_VERSION="11076708"  # Latest as of 2026-04-11
CMDLINE_TOOLS_URL="https://dl.google.com/android/repository/commandlinetools-linux-${CMDLINE_TOOLS_VERSION}_latest.zip"
REQUIRED_JAVA_VERSION=17

echo "================================================"
echo "RemEx Android SDK Setup Script"
echo "================================================"
echo ""

# Check Java version
if command -v java &> /dev/null; then
    JAVA_VERSION=$(java -version 2>&1 | awk -F '"' '/version/ {print $2}' | awk -F'.' '{print $1}')
    echo "✓ Java $JAVA_VERSION detected"
    
    if [ "$JAVA_VERSION" -lt "$REQUIRED_JAVA_VERSION" ]; then
        echo "⚠ Warning: Java $REQUIRED_JAVA_VERSION or higher is recommended"
        echo "  Current version: $JAVA_VERSION"
    fi
else
    echo "⚠ Warning: Java not found. Please install JDK $REQUIRED_JAVA_VERSION+"
    echo "  Ubuntu/Debian: sudo apt install openjdk-17-jdk"
    echo "  Fedora: sudo dnf install java-17-openjdk-devel"
    echo "  Arch: sudo pacman -S jdk17-openjdk"
    exit 1
fi

echo ""
echo "Installing Android SDK to: $ANDROID_SDK_ROOT"
echo ""

# Create SDK directory
mkdir -p "$ANDROID_SDK_ROOT/cmdline-tools"
cd "$ANDROID_SDK_ROOT/cmdline-tools"

# Download command line tools
if [ ! -d "latest" ]; then
    echo "Downloading Android Command Line Tools..."
    wget -q --show-progress "$CMDLINE_TOOLS_URL" -O cmdline-tools.zip
    
    echo "Extracting..."
    unzip -q cmdline-tools.zip
    mv cmdline-tools latest
    rm cmdline-tools.zip
    echo "✓ Command line tools installed"
else
    echo "✓ Command line tools already installed"
fi

# Add SDK to PATH for this session
export PATH="$ANDROID_SDK_ROOT/cmdline-tools/latest/bin:$PATH"
export PATH="$ANDROID_SDK_ROOT/platform-tools:$PATH"

echo ""
echo "Accepting SDK licenses..."
yes | sdkmanager --licenses > /dev/null 2>&1 || true
echo "✓ Licenses accepted"

echo ""
echo "Installing required SDK components..."
echo "  - platform-tools"
echo "  - platforms;android-34"
echo "  - build-tools;34.0.0"
echo ""

sdkmanager "platform-tools" "platforms;android-34" "build-tools;34.0.0"

echo ""
echo "✓ SDK components installed"

# Create local.properties for RemEx.Android project
if [ -n "$GITHUB_WORKSPACE" ]; then
    # Running in GitHub Actions
    LOCAL_PROPS_DIR="$GITHUB_WORKSPACE/RemEx.Android"
elif [ -d "$(dirname "$0")/../RemEx.Android" ]; then
    # Running from scripts/ directory
    LOCAL_PROPS_DIR="$(dirname "$0")/../RemEx.Android"
else
    # Fallback: try to find RemEx.Android directory
    if [ -d "./RemEx.Android" ]; then
        LOCAL_PROPS_DIR="./RemEx.Android"
    elif [ -d "../RemEx.Android" ]; then
        LOCAL_PROPS_DIR="../RemEx.Android"
    else
        echo "⚠ Warning: Could not locate RemEx.Android directory"
        echo "  Please create local.properties manually:"
        echo "  echo 'sdk.dir=$ANDROID_SDK_ROOT' > RemEx.Android/local.properties"
        LOCAL_PROPS_DIR=""
    fi
fi

if [ -n "$LOCAL_PROPS_DIR" ]; then
    echo ""
    echo "Creating local.properties..."
    echo "sdk.dir=$ANDROID_SDK_ROOT" > "$LOCAL_PROPS_DIR/local.properties"
    echo "✓ Created: $LOCAL_PROPS_DIR/local.properties"
fi

echo ""
echo "================================================"
echo "Android SDK setup complete!"
echo "================================================"
echo ""
echo "SDK Location: $ANDROID_SDK_ROOT"
echo ""
echo "To use the SDK in your shell, add to ~/.bashrc or ~/.zshrc:"
echo "  export ANDROID_HOME=$ANDROID_SDK_ROOT"
echo "  export ANDROID_SDK_ROOT=$ANDROID_SDK_ROOT"
echo "  export PATH=\$PATH:$ANDROID_SDK_ROOT/cmdline-tools/latest/bin"
echo "  export PATH=\$PATH:$ANDROID_SDK_ROOT/platform-tools"
echo ""
echo "Verify installation:"
echo "  cd RemEx.Android"
echo "  dotnet build"
echo ""
