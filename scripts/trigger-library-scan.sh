#!/bin/bash
# Trigger Jellyfin library scan after startup
# Runs in background and waits for Jellyfin to be ready
# This ensures newly mounted UNRAR content is detected

echo "[library-scan] Will trigger library scan when Jellyfin is ready..."

(
    # Wait for Jellyfin to be ready (up to 5 minutes)
    for i in {1..60}; do
        HTTP_CODE=$(curl -s -o /dev/null -w '%{http_code}' http://localhost:8096/health 2>/dev/null || echo "000")

        if [ "$HTTP_CODE" = "200" ]; then
            echo "[library-scan] Jellyfin is ready, waiting 10 seconds before scan..."
            sleep 10

            # Trigger library refresh
            curl -s -X POST 'http://localhost:8096/Library/Refresh'
            echo "[library-scan] Library scan triggered"
            exit 0
        fi

        sleep 5
    done

    echo "[library-scan] WARNING: Jellyfin did not become ready within 5 minutes"
) &

echo "[library-scan] Background scan trigger started"
