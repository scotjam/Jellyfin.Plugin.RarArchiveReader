# Jellyfin RAR Archive Reader Plugin

A Jellyfin plugin that enables playback of media files stored inside RAR archives without manual extraction.

> **⚠️ ALPHA SOFTWARE - USE AT YOUR OWN RISK**
>
> This plugin is in early development. It may not work in your setup, could cause issues with your Jellyfin installation, or behave unexpectedly.
>
> **Tested environments:**
> - Linux: linuxserver/jellyfin Docker container on OpenMediaVault (rar2fs mode)
> - Windows: Jellyfin 10.x with STRM streaming mode
>
> **Data loss is possible.** On Linux, this plugin mounts filesystems and modifies directory structures. Back up your media libraries and Jellyfin configuration before installing.
>
> No warranty is provided. You assume all responsibility for any damage or data loss.

## Features

- **Transparent RAR access**: Play video/audio files directly from RAR archives
- **Automatic mounting**: Mounts RAR archives on Jellyfin startup using rar2fs
- **Auto-discovery**: Reads library paths from Jellyfin configuration
- **Multi-part archive support**: Handles `.rar`, `.r00`, `.r01`, and `.partX.rar` naming conventions
- **Fallback streaming**: In-memory streaming when rar2fs is unavailable (Windows)

## Requirements

### Linux (Docker)
- **Jellyfin**: Running in Docker (tested with linuxserver/jellyfin)
- **Docker privileges**: FUSE support requires additional container privileges
- **Linux host**: rar2fs only works on Linux

### Windows
- **Jellyfin**: Windows installation (standard or portable)
- **No additional dependencies**: Uses built-in .strm file streaming

## Installation

### Windows Installation

On Windows, the plugin uses `.strm` files to stream media directly from RAR archives (rar2fs is not available on Windows).

#### Step 1: Download the plugin files

Download or build these files:
- `Jellyfin.Plugin.RarArchiveReader.dll`
- `SharpCompress.dll`

#### Step 2: Create the plugin folder

Create a folder for the plugin:
```
C:\ProgramData\Jellyfin\Server\plugins\RarArchiveReader\
```

#### Step 3: Copy the files

Copy both DLL files to the plugin folder:
```
C:\ProgramData\Jellyfin\Server\plugins\RarArchiveReader\Jellyfin.Plugin.RarArchiveReader.dll
C:\ProgramData\Jellyfin\Server\plugins\RarArchiveReader\SharpCompress.dll
```

#### Step 4: Restart Jellyfin

Stop and start Jellyfin for the plugin to load.

#### Step 5: Organize your media

**Important:** Jellyfin uses folder names to identify TV shows. Your RAR files must be in a folder named after the show:

```
D:\TV Shows\
└── Show Name (Year)\           ← Folder name must match the show
    ├── showname.s01e01.rar     ← RAR archive (can be multi-part)
    ├── showname.s01e01.r00
    ├── showname.s01e01.r01
    └── ...
```

For example:
```
D:\TV Shows\SuperKitties (2022)\superkitties.s02e01.dutch.1080p.web.h264-nlkids.rar
```

#### Step 6: Scan your library

1. Go to Jellyfin Dashboard → Libraries
2. Click on your TV Shows library → Scan Library

The plugin will:
1. Find all RAR archives in your library
2. Extract the list of media files inside each archive
3. Create `.strm` files that point to the streaming endpoint

### Linux Installation (Docker)

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

### Windows (STRM Streaming Mode)

On Windows, the plugin uses `.strm` files to enable playback from RAR archives:

1. **On library scan**, the plugin:
   - Finds all `.rar` files in your library paths
   - Opens each archive and lists the media files inside
   - Creates `.strm` files in the same folder as the RAR

2. **STRM file contents:**
   ```
   http://localhost:8096/RarStream/{encoded-archive-path}/{encoded-entry-path}
   ```

3. **On playback**, Jellyfin:
   - Reads the `.strm` file to get the streaming URL
   - Requests the media from the plugin's `/RarStream` endpoint
   - The plugin extracts and streams the file directly from the RAR

4. **Result:**
   ```
   D:\TV Shows\SuperKitties (2022)\
   ├── superkitties.s02e01.rar         ← Original RAR archive
   ├── superkitties.s02e01.r00         ← Additional RAR parts
   ├── superkitties.s02e01.r01
   └── SuperKitties.S02E01.strm        ← Created by plugin (points to streaming URL)
   ```

5. **Auto-update feature:**
   - If you move RAR files to a different folder, just rescan the library
   - The plugin automatically updates `.strm` files with the new paths
   - Orphaned `.strm` files (pointing to deleted RARs) are cleaned up

### Linux (Docker with rar2fs)

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

### Windows: Show appears as "Programme" with 0 episodes

This happens when Jellyfin can't identify the show from the folder name.

**Fix:** Rename the folder to match the actual show name:
```
D:\kitties\              ← BAD: "kitties" not recognized
D:\SuperKitties (2022)\  ← GOOD: matches show in TVDB/TMDB
```

After renaming, rescan the library.

### Windows: Playback loads forever then fails

1. **Check the `.strm` file path:** Open the `.strm` file in a text editor. The URL should point to the current location of your RAR file.

2. **Verify the endpoint works:** Open the URL from the `.strm` file in your browser. You should get a file download.

3. **Rescan the library:** If you moved the RAR files, rescan the library to update the `.strm` files automatically.

### Windows: No `.strm` files created

1. Check that the RAR archive contains supported video files (`.mkv`, `.mp4`, etc.)
2. Verify the plugin is enabled in Dashboard → Plugins
3. Check Jellyfin logs for errors:
   ```
   C:\ProgramData\Jellyfin\Server\log\
   ```

### Linux: First startup is slow

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

### Windows
- **Folder naming matters**: The parent folder of RAR files must match the show name for Jellyfin to identify it
- **Hardcoded port**: `.strm` files use `localhost:8096` - if Jellyfin runs on a different port, playback won't work
- **Seeking may be slow**: Seeking in large files requires re-reading from the archive start
- **No encrypted archives**: Password-protected RARs not supported

### Linux (Docker)
- **FUSE mounts are container-only**: Not visible via Samba/NFS from host
- **Mounts don't persist**: Re-mounted automatically on container restart
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

### 1.1.0

- **Windows support tested and documented**
- Auto-update `.strm` files when RAR archives are moved
- Automatic cleanup of orphaned `.strm` files
- Improved documentation for Windows installation

### 1.0.0

- Self-contained plugin (auto-installs rar2fs)
- Auto-discovery of Jellyfin library paths
- Automatic library scan after mounting
- Multi-part archive support
- Fallback in-memory streaming
- Configuration UI
