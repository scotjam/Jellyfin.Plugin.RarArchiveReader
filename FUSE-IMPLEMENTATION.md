# FUSE Implementation Guide

This document describes how to implement a FUSE-based approach for mounting RAR archives as virtual filesystems, which provides better performance and integration with Jellyfin.

## Why FUSE?

FUSE (Filesystem in Userspace) offers several advantages over the in-memory streaming approach:

### Benefits

1. **Native Filesystem Integration**: Archive contents appear as real files in the filesystem
2. **Better Seeking**: Media players can seek efficiently without loading entire files into memory
3. **Reduced Memory Usage**: Files are read on-demand rather than loaded entirely into RAM
4. **Transparent to Applications**: Jellyfin doesn't need special handling for archive contents
5. **Better Performance**: OS-level caching and buffer management
6. **Support for Large Files**: No memory constraints for large media files

### Trade-offs

1. **Platform-Specific**: Requires different libraries for Linux/macOS vs Windows
2. **Additional Dependencies**: Needs FUSE/Dokan libraries installed on the system
3. **Mount Management**: Requires managing mount points and cleanup
4. **Permissions**: May require elevated permissions on some systems

## Implementation Options

### Linux/macOS: libfuse

**Option 1: Mono.Fuse (Managed)**
```csharp
// Install: dotnet add package Mono.Fuse
using Mono.Fuse;

public class RarFuseFilesystem : FileSystem
{
    private readonly RarArchiveReader _archive;

    public override Errno OnGetPathStatus(string path, out Stat stat)
    {
        // Return file/directory attributes
    }

    public override Errno OnReadDirectory(string path, out IEnumerable<DirectoryEntry> entries)
    {
        // List directory contents
    }

    public override Errno OnOpenHandle(string path, OpenedPathInfo info)
    {
        // Open file for reading
    }

    public override Errno OnReadHandle(string path, OpenedPathInfo info, byte[] buffer, long offset, out int bytesRead)
    {
        // Read file contents with seeking support
    }

    public override Errno OnReleaseHandle(string path, OpenedPathInfo info)
    {
        // Close file handle
    }
}

// Mount the filesystem
var fs = new RarFuseFilesystem("/path/to/archive.rar");
fs.MountAt("/mnt/rar-mount", new FuseMountOptions());
```

**Option 2: Direct libfuse via P/Invoke**
```csharp
// Requires manual P/Invoke declarations
[DllImport("libfuse.so")]
private static extern int fuse_main(int argc, string[] argv, ref fuse_operations ops, IntPtr user_data);

// Implement fuse_operations callbacks
```

### Windows: Dokan

**DokanNet Library (Recommended for Windows)**
```csharp
// Install: dotnet add package DokanNet
using DokanNet;

public class RarDokanFilesystem : IDokanOperations
{
    private readonly RarArchiveReader _archive;

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        // Return file attributes
        var entry = _archive.GetEntry(fileName);
        fileInfo = new FileInformation
        {
            FileName = Path.GetFileName(fileName),
            Length = entry.Size,
            LastWriteTime = entry.LastModifiedTime,
            Attributes = FileAttributes.ReadOnly
        };
        return NtStatus.Success;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        // List directory contents
        files = _archive.GetEntries()
            .Select(e => new FileInformation { /* ... */ })
            .ToList();
        return NtStatus.Success;
    }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        // Read file with seeking support
        using var stream = _archive.GetEntryStream(fileName);
        stream.Seek(offset, SeekOrigin.Begin);
        bytesRead = stream.Read(buffer, 0, buffer.Length);
        return NtStatus.Success;
    }

    // Implement other required methods...
}

// Mount the filesystem
var fs = new RarDokanFilesystem("/path/to/archive.rar");
fs.Mount(@"R:\", DokanOptions.DebugMode);
```

## Complete Implementation Example

Here's a production-ready FUSE implementation for Linux:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Fuse;
using Mono.Unix.Native;

namespace Jellyfin.Plugin.RarArchiveReader
{
    public class RarFuseFilesystem : FileSystem
    {
        private readonly RarArchiveReader _archive;
        private readonly Dictionary<string, Stream> _openFiles;
        private readonly DateTime _mountTime;

        public RarFuseFilesystem(string archivePath, ILogger logger)
        {
            _archive = new RarArchiveReader(archivePath, logger);
            _archive.Open();
            _openFiles = new Dictionary<string, Stream>();
            _mountTime = DateTime.UtcNow;
        }

        protected override Errno OnGetPathStatus(string path, out Stat stat)
        {
            stat = new Stat();

            if (path == "/")
            {
                // Root directory
                stat.st_mode = NativeConvert.FromFilePermissions(
                    FilePermissions.S_IFDIR |
                    FilePermissions.S_IRUSR |
                    FilePermissions.S_IXUSR |
                    FilePermissions.S_IRGRP |
                    FilePermissions.S_IXGRP |
                    FilePermissions.S_IROTH |
                    FilePermissions.S_IXOTH
                );
                stat.st_nlink = 2;
                return 0;
            }

            var normalizedPath = path.TrimStart('/');
            var entries = _archive.GetEntries();
            var entry = entries.FirstOrDefault(e => e.Key == normalizedPath);

            if (entry != null)
            {
                // File entry
                stat.st_mode = NativeConvert.FromFilePermissions(
                    FilePermissions.S_IFREG |
                    FilePermissions.S_IRUSR |
                    FilePermissions.S_IRGRP |
                    FilePermissions.S_IROTH
                );
                stat.st_nlink = 1;
                stat.st_size = entry.Size;
                stat.st_mtime = ((DateTimeOffset)(entry.LastModifiedTime ?? _mountTime)).ToUnixTimeSeconds();
                return 0;
            }

            // Check if it's a directory path
            var dirPath = normalizedPath.TrimEnd('/') + "/";
            if (entries.Any(e => e.Key.StartsWith(dirPath)))
            {
                stat.st_mode = NativeConvert.FromFilePermissions(
                    FilePermissions.S_IFDIR |
                    FilePermissions.S_IRUSR |
                    FilePermissions.S_IXUSR |
                    FilePermissions.S_IRGRP |
                    FilePermissions.S_IXGRP |
                    FilePermissions.S_IROTH |
                    FilePermissions.S_IXOTH
                );
                stat.st_nlink = 2;
                return 0;
            }

            return Errno.ENOENT;
        }

        protected override Errno OnReadDirectory(string path, out IEnumerable<DirectoryEntry> entries)
        {
            var result = new List<DirectoryEntry>();
            var normalizedPath = path.TrimStart('/');

            if (!string.IsNullOrEmpty(normalizedPath))
            {
                normalizedPath = normalizedPath.TrimEnd('/') + "/";
            }

            var archiveEntries = _archive.GetEntries();
            var seenDirs = new HashSet<string>();

            foreach (var entry in archiveEntries)
            {
                var relativePath = entry.Key;

                if (!string.IsNullOrEmpty(normalizedPath))
                {
                    if (!relativePath.StartsWith(normalizedPath))
                        continue;
                    relativePath = relativePath.Substring(normalizedPath.Length);
                }

                var slashIndex = relativePath.IndexOf('/');
                if (slashIndex >= 0)
                {
                    // Directory
                    var dirName = relativePath.Substring(0, slashIndex);
                    if (seenDirs.Add(dirName))
                    {
                        result.Add(new DirectoryEntry(dirName));
                    }
                }
                else
                {
                    // File
                    result.Add(new DirectoryEntry(relativePath));
                }
            }

            entries = result;
            return 0;
        }

        protected override Errno OnOpenHandle(string path, OpenedPathInfo info)
        {
            if ((info.OpenAccess & OpenFlags.O_WRONLY) != 0 ||
                (info.OpenAccess & OpenFlags.O_RDWR) != 0)
            {
                return Errno.EROFS; // Read-only filesystem
            }

            var normalizedPath = path.TrimStart('/');
            var stream = _archive.GetEntryStream(normalizedPath);

            if (stream == null)
            {
                return Errno.ENOENT;
            }

            lock (_openFiles)
            {
                _openFiles[path] = stream;
            }

            return 0;
        }

        protected override Errno OnReadHandle(string path, OpenedPathInfo info, byte[] buffer, long offset, out int bytesRead)
        {
            bytesRead = 0;

            lock (_openFiles)
            {
                if (!_openFiles.TryGetValue(path, out var stream))
                {
                    return Errno.EBADF;
                }

                try
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                    return 0;
                }
                catch
                {
                    return Errno.EIO;
                }
            }
        }

        protected override Errno OnReleaseHandle(string path, OpenedPathInfo info)
        {
            lock (_openFiles)
            {
                if (_openFiles.TryGetValue(path, out var stream))
                {
                    stream.Dispose();
                    _openFiles.Remove(path);
                }
            }
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_openFiles)
                {
                    foreach (var stream in _openFiles.Values)
                    {
                        stream?.Dispose();
                    }
                    _openFiles.Clear();
                }
                _archive?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
```

## Integration with Jellyfin Plugin

### Mounting Archives on Library Scan

```csharp
public class RarArchiveResolver : IItemResolver
{
    private readonly RarFuseProvider _fuseProvider;

    public BaseItem? ResolvePath(ItemResolveArgs args)
    {
        if (!RarFileSystem.IsRarArchive(args.Path))
            return null;

        // Mount the archive
        var mountPoint = _fuseProvider.MountArchive(args.Path);
        if (mountPoint == null)
            return null;

        // Update args.Path to point to mount point
        // This makes Jellyfin see the contents as regular files
        var newArgs = new ItemResolveArgs(/* ... with mountPoint ... */);

        // Let normal Jellyfin resolvers handle the mounted files
        return null; // or return appropriate item
    }
}
```

### Automatic Mount Management

```csharp
public class RarMountManager : IDisposable
{
    private readonly RarFuseProvider _fuseProvider;
    private readonly Timer _cleanupTimer;

    public RarMountManager(RarFuseProvider fuseProvider)
    {
        _fuseProvider = fuseProvider;

        // Periodically cleanup unused mounts
        _cleanupTimer = new Timer(CleanupUnusedMounts, null,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(30));
    }

    private void CleanupUnusedMounts(object? state)
    {
        // Unmount archives that haven't been accessed recently
        // Track access times and unmount after idle period
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _fuseProvider?.Dispose();
    }
}
```

## Installation Requirements

### Linux

```bash
# Install FUSE development libraries
sudo apt-get install fuse libfuse-dev

# Install Mono.Fuse (if using managed approach)
dotnet add package Mono.Fuse
```

### macOS

```bash
# Install macFUSE
brew install --cask macfuse

# Install Mono.Fuse
dotnet add package Mono.Fuse
```

### Windows

```bash
# Install Dokan
# Download from: https://github.com/dokan-dev/dokany/releases

# Install DokanNet
dotnet add package DokanNet
```

## Testing

```bash
# Mount a test archive
mkdir /tmp/test-mount
dotnet run -- mount /path/to/archive.rar /tmp/test-mount

# List contents
ls -la /tmp/test-mount

# Test media playback
mpv /tmp/test-mount/video.mkv

# Unmount
fusermount -u /tmp/test-mount  # Linux
umount /tmp/test-mount         # macOS
```

## Production Considerations

1. **Error Handling**: Handle corrupted archives, missing parts, I/O errors
2. **Performance**: Cache directory listings, implement readahead
3. **Security**: Validate paths, prevent directory traversal
4. **Resource Limits**: Limit concurrent mounts, implement mount timeouts
5. **Logging**: Comprehensive logging for debugging mount issues
6. **Cleanup**: Ensure mounts are unmounted on plugin shutdown
7. **Permissions**: Handle permission requirements gracefully

## Alternative: rar2fs

Instead of implementing FUSE yourself, consider using the existing `rar2fs` utility:

```bash
# Install rar2fs
sudo apt-get install rar2fs

# Mount archive
rar2fs /path/to/archive.rar /mnt/mount-point

# Use in Jellyfin
# Point Jellyfin library to /mnt/mount-point
```

The plugin could shell out to `rar2fs` for mounting instead of implementing FUSE directly.

## Conclusion

A FUSE-based implementation provides the best user experience for RAR archive support in Jellyfin, with transparent integration and optimal performance. The choice between implementing FUSE directly vs. using `rar2fs` depends on:

- **Control**: Direct implementation offers more control
- **Simplicity**: Using `rar2fs` is simpler but requires external dependency
- **Cross-platform**: Direct implementation can support Windows via Dokan
- **Maintenance**: `rar2fs` is maintained separately
