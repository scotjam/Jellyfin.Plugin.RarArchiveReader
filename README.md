# Jellyfin RAR Archive Reader Plugin

A Jellyfin plugin that enables playback of media files stored inside RAR archives without manual extraction.

> **⚠️ ALPHA SOFTWARE - USE AT YOUR OWN RISK**
>
> This plugin is in early development and has only been tested in a single environment (linuxserver/jellyfin Docker container on OpenMediaVault). It may not work in your setup, could cause issues with your Jellyfin installation, or behave unexpectedly.
>
> **Data loss is possible.** This plugin mounts filesystems and modifies directory structures. Back up your media libraries and Jellyfin configuration before installing.
>
> **The SharpCompress fallback mode (used on Windows or when rar2fs is unavailable) is completely untested.** Only the rar2fs mounting approach on Linux has been verified to work.
>
> No warranty is provided. You assume all responsibility for any damage or data loss.

## Features

- **Transparent RAR access**: Play video/audio files directly from RAR archives
- **Automatic mounting**: Mounts RAR archives on Jellyfin startup using rar2fs
- **Auto-discovery**: Reads library paths from Jellyfin configuration
- **Multi-part archive support**: Handles `.rar`, `.r00`, `.r01`, and `.partX.rar` naming conventions
- **Fallback streaming**: In-memory streaming when rar2fs is unavailable (Windows)

## Requirements

- **Jellyfin**: Running in Docker (tested with linuxserver/jellyfin)
- **Docker privileges**: FUSE support requires additional container privileges
- **Linux host**: rar2fs only works on Linux

## Installation

### Step 1: Clone the repository on your Docker host

SSH into your Docker host and clone the repo:

```bash
ssh root@your-server-ip
cd /tmp
git clone https://github.com/scotjam/Jellyfin.Plugin.RarArchiveReader.git
cd Jellyfin.Plugin.RarArchiveReader
```

### Step 2: Run the installer

Run the installer script, providing the path to your Jellyfin config directory:

```bash
bash install.sh /path/to/jellyfin/config
```

To find your Jellyfin config path:
```bash
docker inspect jellyfin --format '{{range .Mounts}}{{if eq .Destination "/config"}}{{.Source}}{{end}}{{end}}'
```

The installer will:
- Copy plugin DLLs to the plugins directory
- Copy the rar2fs setup script to custom-cont-init.d
- Set correct permissions

### Step 3: Configure Docker with FUSE privileges

Your Jellyfin container needs FUSE access. Add these settings to your `docker-compose.yml`:

```yaml
services:
  jellyfin:
    image: lscr.io/linuxserver/jellyfin:latest
    # ... your existing config ...
    devices:
      - /dev/dri:/dev/dri    # You likely already have this line
      - /dev/fuse:/dev/fuse  # ADD this line for FUSE support
    cap_add:
      - SYS_ADMIN
    security_opt:
      - apparmor:unconfined
    volumes:
      # ... your existing volumes ...
      - /path/to/jellyfin/config/custom-cont-init.d:/custom-cont-init.d:ro  # ADD this line
```

**Important:**
- Add `/dev/fuse` to your existing `devices` section (don't remove `/dev/dri` if you have it)
- The `custom-cont-init.d` volume mount is required for the rar2fs install script to run on container startup

If you're using Portainer or OpenMediaVault, you may need to recreate the container with these settings. Example docker run command:

```bash
docker run -d \
  --name jellyfin \
  --restart unless-stopped \
  -e PUID=1000 \
  -e PGID=100 \
  -e TZ=Your/Timezone \
  --device /dev/dri:/dev/dri \
  --device /dev/fuse:/dev/fuse \
  --cap-add SYS_ADMIN \
  --security-opt apparmor:unconfined \
  -p 8096:8096 \
  -v /path/to/config:/config \
  -v /path/to/cache:/cache \
  -v /path/to/media:/media \
  -v /path/to/config/custom-cont-init.d:/custom-cont-init.d:ro \
  lscr.io/linuxserver/jellyfin:latest
```

### Step 4: Restart Jellyfin

```bash
docker-compose down && docker-compose up -d
# or if using docker run:
docker restart jellyfin
```

**First startup** takes ~3-5 minutes to build rar2fs from source. Subsequent startups are fast (~10 seconds).

### Step 5: Verify installation

Check the container logs to confirm rar2fs installed successfully:

```bash
docker logs jellyfin 2>&1 | grep -i rar2fs
```

You should see:
```
[rar2fs] Installation complete: rar2fs v1.29.7 ...
```

### Manual Installation (Alternative)

<details>
<summary>Click to expand manual steps</summary>

If you prefer not to use the installer script, you can manually copy files:

1. SSH into your Docker host:
```bash
ssh root@your-server-ip
```

2. Clone the repository:
```bash
cd /tmp
git clone https://github.com/scotjam/Jellyfin.Plugin.RarArchiveReader.git
cd Jellyfin.Plugin.RarArchiveReader
```

3. Create directories:
```bash
mkdir -p /path/to/jellyfin/config/data/plugins/RarArchiveReader
mkdir -p /path/to/jellyfin/config/custom-cont-init.d
```

4. Copy plugin files:
```bash
cp Jellyfin.Plugin.RarArchiveReader.dll /path/to/jellyfin/config/data/plugins/RarArchiveReader/
cp SharpCompress.dll /path/to/jellyfin/config/data/plugins/RarArchiveReader/
```

5. Copy rar2fs setup script:
```bash
cp scripts/install-rar2fs.sh /path/to/jellyfin/config/custom-cont-init.d/
chmod +x /path/to/jellyfin/config/custom-cont-init.d/install-rar2fs.sh
```

6. Set permissions:
```bash
chown -R 1000:100 /path/to/jellyfin/config/data/plugins/RarArchiveReader
```

7. Configure Docker with FUSE privileges (see Step 3 above)

8. Restart Jellyfin (see Step 4 above)

</details>

After startup, the plugin will:
1. Discover your library paths from Jellyfin's configuration
2. Find all directories containing RAR archives
3. Mount each directory with rar2fs to an `UNRAR` subfolder

## How It Works

1. **On container startup**, the install script:
   - Builds and installs rar2fs (first time only, ~3-5 min)
   - Configures FUSE for non-root users (every restart)

2. **On Jellyfin startup**, the plugin:
   - Reads library paths from `/config/data/root/default/*/options.xml`
   - Finds all directories containing `.rar` files
   - Mounts each directory with rar2fs to an `UNRAR` subfolder

3. **Result:**
   ```
   /movies/Some.Movie/
   ├── some.movie.rar
   ├── some.movie.r00
   ├── some.movie.r01
   └── UNRAR/
       └── Some.Movie.mkv  ← Playable in Jellyfin
   ```

## Configuration

Navigate to **Dashboard** → **Plugins** → **RAR Archive Reader**:

| Setting | Description | Default |
|---------|-------------|---------|
| Enable automatic scanning | Auto-detect RAR archives during library scans | Enabled |
| Prefer rar2fs | Use rar2fs when available | Enabled |
| Mount idle timeout | Auto-unmount after inactivity (minutes) | 30 |
| Maximum file size (MB) | Max size for fallback streaming | 500 |
| Supported video extensions | Video file types to recognize | `.mkv,.mp4,.avi,...` |

## Troubleshooting

### First startup is slow

The first startup takes ~3-5 minutes to build rar2fs from source. Subsequent startups are fast.

### Mounts not appearing

```bash
# Check container logs
docker logs jellyfin | grep -i rar

# Verify FUSE device
docker exec jellyfin ls /dev/fuse

# Check rar2fs installed
docker exec jellyfin which rar2fs

# Check active mounts
docker exec jellyfin mount | grep rar2fs
```

### Permission denied

Ensure your `docker-compose.yml` has all required privileges:
```yaml
devices:
  - /dev/fuse:/dev/fuse
cap_add:
  - SYS_ADMIN
security_opt:
  - apparmor:unconfined
```

### Library not detecting files

1. Check Dashboard → Scheduled Tasks → "Mount RAR Archives" status
2. Run the task manually if needed
3. Verify UNRAR folders have content:
   ```bash
   docker exec jellyfin ls /path/to/movie/UNRAR/
   ```

## Advanced: Standalone Scripts

For users who prefer container-level setup (mounts created before Jellyfin starts), standalone scripts are available in the `scripts/` folder. See [scripts/README.md](scripts/README.md) for details.

## Limitations

- **FUSE mounts are container-only**: Not visible via Samba/NFS from host
- **Mounts don't persist**: Re-mounted automatically on container restart
- **Linux only**: rar2fs requires Linux; Windows uses fallback streaming
- **No encrypted archives**: Password-protected RARs not supported

## Building from Source

```bash
# Prerequisites: .NET 8.0 SDK
dotnet build -c Release

# Output: bin/Release/net8.0/
```

## Technical Details

### Architecture

- **RarArchiveStartupTask**: Self-contained task that installs rar2fs, mounts archives, triggers scans
- **Rar2fsManager**: Manages mounts and rar2fs installation
- **RarFileSystem**: Virtual filesystem for fallback mode
- **SharpCompress**: .NET library for reading RAR archives

### Library Path Discovery

The plugin reads Jellyfin's library configuration from:
- `/config/data/root/default/*/options.xml` (linuxserver container)
- `/var/lib/jellyfin/root/default/*/options.xml` (native install)

### Mount Points

Archives are mounted in-place using UNRAR subfolders:
```
/source/directory/UNRAR/  ← mount point
```

## Related

- [Jellyfin Issue #85](https://github.com/jellyfin/jellyfin/issues/85) - Original feature request
- [rar2fs](https://github.com/hasse69/rar2fs) - FUSE filesystem for RAR (GPL v3)
- [SharpCompress](https://github.com/adamhathcock/sharpcompress) - .NET archive library (MIT)

## License

MIT License

Copyright (c) 2026

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

**Note:** This plugin optionally uses [rar2fs](https://github.com/hasse69/rar2fs), which is licensed under GPL v3. rar2fs is called as an external process and is not distributed with this plugin.

## Changelog

### 1.0.0

- Self-contained plugin (auto-installs rar2fs)
- Auto-discovery of Jellyfin library paths
- Automatic library scan after mounting
- Multi-part archive support
- Fallback in-memory streaming
- Configuration UI
