#!/bin/bash
# Install rar2fs for RAR archive mounting support
# This script builds rar2fs from source if not already installed
# Runs once on first container startup, subsequent starts are fast

set -e

# Configure FUSE for non-root users (needed even if rar2fs is already installed)
configure_fuse() {
    echo "[rar2fs] Configuring FUSE for non-root users..."

    # Install fuse3 userspace tools if not present
    if ! command -v fusermount3 &> /dev/null; then
        apt-get update
        apt-get install -y --no-install-recommends fuse3
    fi

    # Create symlink for fusermount (rar2fs uses FUSE 2.x which expects 'fusermount')
    if [ ! -e /usr/bin/fusermount ]; then
        ln -sf /usr/bin/fusermount3 /usr/bin/fusermount
        echo "[rar2fs] Created fusermount symlink"
    fi

    # Enable user_allow_other in fuse.conf (allows non-root to use allow_other option)
    if [ -f /etc/fuse.conf ]; then
        if ! grep -q "^user_allow_other" /etc/fuse.conf; then
            sed -i 's/#user_allow_other/user_allow_other/' /etc/fuse.conf
            echo "[rar2fs] Enabled user_allow_other in /etc/fuse.conf"
        fi
    fi
}

# Always configure FUSE (container restarts lose this config)
configure_fuse

if command -v rar2fs &> /dev/null; then
    echo "[rar2fs] Already installed: $(rar2fs --version 2>&1 | head -1)"
    exit 0
fi

echo "[rar2fs] Installing build dependencies..."
apt-get update
apt-get install -y --no-install-recommends \
    build-essential \
    autoconf \
    automake \
    libtool \
    libfuse-dev \
    fuse3 \
    wget \
    ca-certificates

# Create temp build directory
BUILD_DIR=$(mktemp -d)
cd "$BUILD_DIR"

echo "[rar2fs] Downloading unrar source..."
wget -q https://www.rarlab.com/rar/unrarsrc-7.1.6.tar.gz
tar xzf unrarsrc-7.1.6.tar.gz
cd unrar
make -j$(nproc) lib
make install-lib
cd ..

echo "[rar2fs] Downloading rar2fs source..."
wget -q https://github.com/hasse69/rar2fs/archive/refs/tags/v1.29.7.tar.gz -O rar2fs-1.29.7.tar.gz
tar xzf rar2fs-1.29.7.tar.gz
cd rar2fs-1.29.7

echo "[rar2fs] Building rar2fs..."
autoreconf -i
./configure --with-unrar=../unrar --with-unrar-lib=/usr/lib
make -j$(nproc)
make install

# Cleanup
cd /
rm -rf "$BUILD_DIR"

echo "[rar2fs] Installation complete: $(rar2fs --version 2>&1 | head -1)"
echo "[rar2fs] FUSE configured for non-root user access"
