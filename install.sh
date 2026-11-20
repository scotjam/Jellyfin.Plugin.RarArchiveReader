#!/bin/bash
# Jellyfin RAR Archive Reader Plugin - Installer
# Run this script on your Docker host to install the plugin
#
# Usage: ./install.sh /path/to/jellyfin/config [10.11|12]
#
# The second argument picks the build for your Jellyfin version: 10.11 uses
# bin/Release/net9.0, 12 uses bin/Release/net10.0. It can be left out when
# only one of the two has been built.
#
# Example: ./install.sh /srv/jellyfin/config 12

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}Jellyfin RAR Archive Reader Installer${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""

# Get the directory where this script is located
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Check for config path argument
if [ -z "$1" ]; then
    echo -e "${YELLOW}Usage: $0 /path/to/jellyfin/config [10.11|12]${NC}"
    echo ""
    echo "Common locations:"
    echo "  - /srv/jellyfin/config"
    echo "  - /opt/jellyfin/config"
    echo "  - /docker/jellyfin/config"
    echo "  - ~/.config/jellyfin"
    echo ""

    # Try to auto-detect
    for path in /srv/*/jellyfin/config /opt/jellyfin/config /docker/jellyfin/config; do
        if [ -d "$path" ]; then
            echo -e "Found: ${GREEN}$path${NC}"
        fi
    done

    exit 1
fi

JELLYFIN_CONFIG="$1"

# Validate config path
if [ ! -d "$JELLYFIN_CONFIG" ]; then
    echo -e "${RED}Error: Directory not found: $JELLYFIN_CONFIG${NC}"
    exit 1
fi

echo -e "Jellyfin config path: ${GREEN}$JELLYFIN_CONFIG${NC}"
echo ""

# Step 1: Create plugin directory
echo -e "${YELLOW}[1/3] Creating plugin directory...${NC}"

PLUGIN_DIR="$JELLYFIN_CONFIG/data/plugins/RarArchiveReader"

mkdir -p "$PLUGIN_DIR"

echo "  Created: $PLUGIN_DIR"

# Step 2: Copy plugin files
echo -e "${YELLOW}[2/3] Installing plugin...${NC}"

case "$2" in
    10.11*) TFM=net9.0 ;;
    12*)    TFM=net10.0 ;;
    "")
        BUILT=""
        for t in net9.0 net10.0; do
            [ -f "$SCRIPT_DIR/bin/Release/$t/Jellyfin.Plugin.RarArchiveReader.dll" ] && BUILT="$BUILT $t"
        done
        set -- "$1" $BUILT
        if [ $# -eq 2 ]; then
            TFM="$2"
        elif [ $# -gt 2 ]; then
            echo -e "${RED}Error: both builds exist. Say which Jellyfin version you run:${NC}"
            echo "  $0 $JELLYFIN_CONFIG 10.11   (Jellyfin 10.11.x)"
            echo "  $0 $JELLYFIN_CONFIG 12      (Jellyfin 12.x)"
            exit 1
        fi
        ;;
    *)
        echo -e "${RED}Error: unknown Jellyfin version '$2' (use 10.11 or 12)${NC}"
        exit 1
        ;;
esac

BUILD_DIR="$SCRIPT_DIR/bin/Release/$TFM"
if [ -z "$TFM" ] || [ ! -f "$BUILD_DIR/Jellyfin.Plugin.RarArchiveReader.dll" ]; then
    echo -e "${RED}Error: Plugin DLLs not found. Please build the plugin first:${NC}"
    echo "  dotnet build -c Release"
    exit 1
fi

cp "$BUILD_DIR/Jellyfin.Plugin.RarArchiveReader.dll" "$PLUGIN_DIR/"
cp "$BUILD_DIR/SharpCompress.dll" "$PLUGIN_DIR/"
echo "  Copied plugin DLLs from bin/Release/$TFM/"

# Step 3: Set permissions
echo -e "${YELLOW}[3/3] Setting permissions...${NC}"

# Try to set ownership to match Jellyfin user (usually 1000:100 for linuxserver)
if command -v chown &> /dev/null; then
    chown -R 1000:100 "$PLUGIN_DIR" 2>/dev/null || true
    echo "  Set plugin ownership to 1000:100"
fi

chmod -R 755 "$PLUGIN_DIR"
echo "  Set plugin permissions"

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}Installation complete!${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo "Next steps:"
echo ""
echo "1. Restart Jellyfin:"
echo ""
echo -e "${YELLOW}   docker-compose down && docker-compose up -d${NC}"
echo ""
echo "2. Go to Dashboard > Scheduled Tasks > Process RAR Archives > Run"
echo ""
echo "The plugin will create .strm files for any RAR archives"
echo "containing media files in your library paths."
echo ""
