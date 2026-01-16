#!/bin/bash
# Jellyfin RAR Archive Reader Plugin - Installer
# Run this script on your Docker host to install the plugin
#
# Usage: ./install.sh /path/to/jellyfin/config
#
# Example: ./install.sh /srv/jellyfin/config

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
    echo -e "${YELLOW}Usage: $0 /path/to/jellyfin/config${NC}"
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

# Step 1: Create directories
echo -e "${YELLOW}[1/4] Creating directories...${NC}"

PLUGIN_DIR="$JELLYFIN_CONFIG/data/plugins/RarArchiveReader"
INIT_DIR="$JELLYFIN_CONFIG/custom-cont-init.d"

mkdir -p "$PLUGIN_DIR"
mkdir -p "$INIT_DIR"

echo "  Created: $PLUGIN_DIR"
echo "  Created: $INIT_DIR"

# Step 2: Copy plugin files
echo -e "${YELLOW}[2/4] Installing plugin...${NC}"

if [ -f "$SCRIPT_DIR/bin/Release/net8.0/Jellyfin.Plugin.RarArchiveReader.dll" ]; then
    cp "$SCRIPT_DIR/bin/Release/net8.0/Jellyfin.Plugin.RarArchiveReader.dll" "$PLUGIN_DIR/"
    cp "$SCRIPT_DIR/bin/Release/net8.0/SharpCompress.dll" "$PLUGIN_DIR/"
    echo "  Copied plugin DLLs from bin/Release/net8.0/"
elif [ -f "$SCRIPT_DIR/Jellyfin.Plugin.RarArchiveReader.dll" ]; then
    cp "$SCRIPT_DIR/Jellyfin.Plugin.RarArchiveReader.dll" "$PLUGIN_DIR/"
    cp "$SCRIPT_DIR/SharpCompress.dll" "$PLUGIN_DIR/"
    echo "  Copied plugin DLLs from script directory"
else
    echo -e "${RED}Error: Plugin DLLs not found. Please build the plugin first:${NC}"
    echo "  dotnet build -c Release"
    exit 1
fi

# Step 3: Copy install script
echo -e "${YELLOW}[3/4] Installing rar2fs setup script...${NC}"

cp "$SCRIPT_DIR/scripts/install-rar2fs.sh" "$INIT_DIR/"
chmod +x "$INIT_DIR/install-rar2fs.sh"
echo "  Copied install-rar2fs.sh to custom-cont-init.d"

# Step 4: Set permissions
echo -e "${YELLOW}[4/4] Setting permissions...${NC}"

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
echo "1. Make sure your docker-compose.yml has these settings:"
echo ""
echo -e "${YELLOW}   devices:"
echo "     - /dev/fuse:/dev/fuse"
echo "   cap_add:"
echo "     - SYS_ADMIN"
echo "   security_opt:"
echo "     - apparmor:unconfined"
echo "   volumes:"
echo -e "     - $INIT_DIR:/custom-cont-init.d:ro${NC}"
echo ""
echo "2. Restart Jellyfin:"
echo ""
echo -e "${YELLOW}   docker-compose down && docker-compose up -d${NC}"
echo ""
echo "First startup will take 3-5 minutes to build rar2fs."
echo "Subsequent startups are fast (~10 seconds)."
echo ""
