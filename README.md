# Jellyfin RAR Archive Reader Plugin

A Jellyfin plugin that enables playback of media files stored inside RAR archives without manual extraction.

> **Alpha Software** - This plugin is in early development. Back up your media libraries and Jellyfin configuration before installing.

## Features

- **Transparent RAR access**: Play video/audio files directly from RAR archives
- **STRM streaming**: Creates `.strm` files that stream media from RAR archives on demand
- **Auto-discovery**: Reads library paths from Jellyfin configuration
- **Multi-part archive support**: Handles `.rar`, `.r00`, `.r01`, and `.partX.rar` naming conventions
- **Auto-update**: Automatically updates `.strm` files when RAR archives are moved
- **Orphan cleanup**: Removes `.strm` files when their RAR archives are deleted
- **Cross-platform**: Works on both Windows and Linux (Docker)

## Requirements

- **Jellyfin**: Windows or Linux installation (tested with linuxserver/jellyfin Docker)
- **No additional dependencies**: Uses built-in STRM file streaming

## Installation

### Step 1: Download the plugin files

Download or build these files:
- `Jellyfin.Plugin.RarArchiveReader.dll`
- `SharpCompress.dll`

To build from source:
```bash
dotnet build -c Release
# Output: bin/Release/net8.0/
```

### Step 2: Copy files to the plugin directory

**Windows:**
```
C:\ProgramData\Jellyfin\Server\plugins\RarArchiveReader\Jellyfin.Plugin.RarArchiveReader.dll
C:\ProgramData\Jellyfin\Server\plugins\RarArchiveReader\SharpCompress.dll
```

**Linux (Docker):**
```bash
# Find your Jellyfin config path:
docker inspect jellyfin --format '{{range .Mounts}}{{if eq .Destination "/config"}}{{.Source}}{{end}}{{end}}'

# Copy the DLLs:
mkdir -p /path/to/jellyfin/config/data/plugins/RarArchiveReader
cp Jellyfin.Plugin.RarArchiveReader.dll /path/to/jellyfin/config/data/plugins/RarArchiveReader/
cp SharpCompress.dll /path/to/jellyfin/config/data/plugins/RarArchiveReader/
chown -R 1000:100 /path/to/jellyfin/config/data/plugins/RarArchiveReader
```

Or use the installer script:
```bash
bash install.sh /path/to/jellyfin/config
```

### Step 3: Restart Jellyfin

Stop and start Jellyfin for the plugin to load.

### Step 4: Organize your media

Jellyfin uses folder names to identify TV shows. Your RAR files must be in a folder named after the show:

```
TV Shows/
  Show Name (Year)/
    showname.s01e01.rar
    showname.s01e01.r00
    showname.s01e01.r01
```

### Step 5: Scan your library

1. Go to Jellyfin Dashboard > Libraries
2. Click on your TV Shows library > Scan Library

The plugin will:
1. Find all RAR archives in your library
2. Extract the list of media files inside each archive
3. Create `.strm` files that point to the streaming endpoint

## How It Works

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
   TV Shows/SuperKitties (2022)/
     superkitties.s02e01.rar         <- Original RAR archive
     superkitties.s02e01.r00         <- Additional RAR parts
     superkitties.s02e01.r01
     SuperKitties.S02E01.strm        <- Created by plugin (points to streaming URL)
   ```

5. **Auto-update feature:**
   - If you move RAR files to a different folder, just rescan the library
   - The plugin automatically updates `.strm` files with the new paths
   - Orphaned `.strm` files (pointing to deleted RARs) are cleaned up

## Configuration

Navigate to **Dashboard** > **Plugins** > **RAR Archive Reader**:

| Setting | Description | Default |
|---------|-------------|---------|
| Enable automatic scanning | Auto-detect RAR archives during library scans | Enabled |
| Maximum file size (MB) | Max size for fallback streaming | 500 |
| Cache archive metadata | Cache file list for faster access | Enabled |
| Supported video extensions | Video file types to recognize | `.mkv,.mp4,.avi,...` |
| Supported audio extensions | Audio file types to recognize | `.mp3,.flac,.wav,...` |
| Supported image extensions | Image file types to recognize | `.jpg,.png,.gif,...` |

## Troubleshooting

### Show appears as "Programme" with 0 episodes

This happens when Jellyfin can't identify the show from the folder name.

**Fix:** Rename the folder to match the actual show name:
```
D:\kitties\              <- BAD: "kitties" not recognized
D:\SuperKitties (2022)\  <- GOOD: matches show in TVDB/TMDB
```

After renaming, rescan the library.

### Playback loads forever then fails

1. **Check the `.strm` file path:** Open the `.strm` file in a text editor. The URL should point to the current location of your RAR file.

2. **Verify the endpoint works:** Open the URL from the `.strm` file in your browser. You should get a file download.

3. **Rescan the library:** If you moved the RAR files, rescan the library to update the `.strm` files automatically.

### No `.strm` files created

1. Check that the RAR archive contains supported video files (`.mkv`, `.mp4`, etc.)
2. Verify the plugin is enabled in Dashboard > Plugins
3. Check Jellyfin logs for errors

### Permission denied (Linux)

Ensure the Jellyfin user can read your media directories:
```bash
chown -R 1000:100 /path/to/media
```

## Advanced: Standalone Scripts

Optional utility scripts are available in the `scripts/` folder. See [scripts/README.md](scripts/README.md) for details.

## Limitations

- **Folder naming matters**: The parent folder of RAR files must match the show name for Jellyfin to identify it
- **Hardcoded port**: `.strm` files use `localhost:8096` - if Jellyfin runs on a different port, playback won't work
- **Seeking may be slow**: Seeking in large files requires re-reading from the archive start
- **No encrypted archives**: Password-protected RARs not supported

## Building from Source

```bash
# Prerequisites: .NET 8.0 SDK
dotnet build -c Release

# Output: bin/Release/net8.0/
```

## Technical Details

### Architecture

- **RarArchiveStartupTask / RarArchivePostScanTask**: Scan libraries and create STRM files for RAR archives
- **RarStreamController**: HTTP endpoint that streams media from RAR archives
- **RarFileSystem**: Virtual filesystem for archive reading
- **RarArchiveReader**: SharpCompress-based archive reader
- **RarBufferedStream / RarMediaStreamProvider**: Buffered streaming for playback

### Library Path Discovery

The plugin reads Jellyfin's library configuration from:
- `/config/data/root/default/*/options.xml` (linuxserver container)
- `/var/lib/jellyfin/root/default/*/options.xml` (native Linux install)
- `%LOCALAPPDATA%\jellyfin\root\default\*\options.xml` (Windows portable)
- `%PROGRAMDATA%\Jellyfin\Server\root\default\*\options.xml` (Windows standard)

## Related

- [Jellyfin Issue #85](https://github.com/jellyfin/jellyfin/issues/85) - Original feature request
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

## Changelog

### 2.0.0

- **Simplified to STRM-only mode** - removed rar2fs/FUSE mounting code
- No Docker privileges required (no /dev/fuse, SYS_ADMIN, apparmor)
- Unified installation for Windows and Linux (just copy DLLs)
- Removed dependencies: rar2fs, FUSE, custom init scripts

### 1.1.0

- Windows support tested and documented
- Auto-update `.strm` files when RAR archives are moved
- Automatic cleanup of orphaned `.strm` files

### 1.0.0

- Initial release
- Multi-part archive support
- Configuration UI
