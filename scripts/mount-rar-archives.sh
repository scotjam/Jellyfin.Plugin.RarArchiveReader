#!/bin/bash
# Mount RAR archives on container startup
# Reads library paths from Jellyfin configuration automatically
# Creates UNRAR subfolders in directories containing .rar files

echo "[mount-rar] Starting RAR archive mounting..."

# Check if rar2fs is available
if ! command -v rar2fs &> /dev/null; then
    echo "[mount-rar] ERROR: rar2fs not installed, skipping mount"
    exit 1
fi

# Get library paths from Jellyfin config files
LIBRARY_PATHS=$(grep -rh '<Path>' /config/data/root/default/*/options.xml 2>/dev/null | sed 's/.*<Path>\(.*\)<\/Path>.*/\1/' | sort -u)

if [ -z "$LIBRARY_PATHS" ]; then
    echo "[mount-rar] No library paths found in Jellyfin config"
    exit 0
fi

echo "[mount-rar] Found library paths:"
echo "$LIBRARY_PATHS" | while read path; do echo "  - $path"; done

MOUNT_COUNT=0
SKIP_COUNT=0

echo "$LIBRARY_PATHS" | while read MEDIA_PATH; do
    if [ -z "$MEDIA_PATH" ] || [ ! -d "$MEDIA_PATH" ]; then
        continue
    fi

    echo "[mount-rar] Scanning: $MEDIA_PATH"

    # Find directories containing .rar files
    find "$MEDIA_PATH" -name "*.rar" -type f 2>/dev/null | while read RAR_FILE; do
        DIR=$(dirname "$RAR_FILE")
        UNRAR_DIR="$DIR/UNRAR"

        # Skip if already mounted
        if mountpoint -q "$UNRAR_DIR" 2>/dev/null; then
            continue
        fi

        # Create UNRAR directory if needed
        mkdir -p "$UNRAR_DIR"

        # Mount with rar2fs
        if rar2fs -o allow_other "$DIR" "$UNRAR_DIR" 2>/dev/null; then
            echo "[mount-rar] Mounted: $UNRAR_DIR"
        fi
    done
done

# Count total mounts
TOTAL_MOUNTS=$(mount | grep -c rar2fs || echo "0")
echo "[mount-rar] Complete. Total rar2fs mounts: $TOTAL_MOUNTS"
