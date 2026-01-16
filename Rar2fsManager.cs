using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Manages mounting and unmounting RAR archives using rar2fs utility.
    /// Falls back to in-memory streaming if rar2fs is not available.
    /// </summary>
    public class Rar2fsManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly RarFileSystem _fallbackFileSystem;
        private readonly Dictionary<string, MountInfo> _mounts;
        private readonly object _lock = new object();
        private bool _disposed;
        private bool? _rar2fsAvailable;

        /// <summary>
        /// Initializes a new instance of the <see cref="Rar2fsManager"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="fallbackFileSystem">Fallback file system for in-memory streaming.</param>
        public Rar2fsManager(ILogger logger, RarFileSystem fallbackFileSystem)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fallbackFileSystem = fallbackFileSystem ?? throw new ArgumentNullException(nameof(fallbackFileSystem));
            _mounts = new Dictionary<string, MountInfo>();
        }

        /// <summary>
        /// Gets the base directory for mount points.
        /// </summary>
        public string BaseMountDirectory
        {
            get
            {
                var config = Plugin.Instance?.Configuration;
                if (!string.IsNullOrEmpty(config?.MountPointBase))
                {
                    return config.MountPointBase;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                    RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return "/tmp/jellyfin-rar-mounts";
                }
                else
                {
                    return Path.Combine(Path.GetTempPath(), "JellyfinRarMounts");
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether rar2fs is available on the system.
        /// </summary>
        public bool IsRar2fsAvailable
        {
            get
            {
                if (_rar2fsAvailable.HasValue)
                {
                    return _rar2fsAvailable.Value;
                }

                _rar2fsAvailable = CheckRar2fsAvailability();
                return _rar2fsAvailable.Value;
            }
        }

        /// <summary>
        /// Mounts a RAR archive using rar2fs or fallback to in-memory streaming.
        /// Note: rar2fs mounts the directory containing RAR files, not individual files.
        /// </summary>
        /// <param name="archivePath">Path to the RAR archive file.</param>
        /// <returns>The mount point path, or null if mounting failed.</returns>
        public string? MountArchive(string archivePath)
        {
            if (!File.Exists(archivePath))
            {
                _logger.LogError("Archive file not found: {Path}", archivePath);
                return null;
            }

            // rar2fs mounts directories, not individual files
            var sourceDirectory = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrEmpty(sourceDirectory))
            {
                _logger.LogError("Could not determine directory for archive: {Path}", archivePath);
                return null;
            }

            lock (_lock)
            {
                // Check if the directory is already mounted (any RAR in this dir shares the mount)
                if (_mounts.TryGetValue(sourceDirectory, out var existingMount))
                {
                    existingMount.LastAccessed = DateTime.UtcNow;
                    _logger.LogDebug("Directory already mounted: {Dir} -> {Mount}", sourceDirectory, existingMount.MountPoint);
                    return existingMount.MountPoint;
                }

                // Try rar2fs first
                if (IsRar2fsAvailable)
                {
                    var mountPoint = MountWithRar2fs(sourceDirectory);
                    if (mountPoint != null)
                    {
                        _mounts[sourceDirectory] = new MountInfo
                        {
                            ArchivePath = sourceDirectory,
                            MountPoint = mountPoint,
                            MountedAt = DateTime.UtcNow,
                            LastAccessed = DateTime.UtcNow,
                            UsesRar2fs = true
                        };
                        return mountPoint;
                    }

                    _logger.LogWarning("Failed to mount with rar2fs, falling back to in-memory streaming");
                }

                // Fallback to in-memory streaming
                _logger.LogInformation("Using in-memory streaming for: {Path}", archivePath);
                _mounts[sourceDirectory] = new MountInfo
                {
                    ArchivePath = sourceDirectory,
                    MountPoint = sourceDirectory, // Use original directory for fallback
                    MountedAt = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow,
                    UsesRar2fs = false
                };
                return sourceDirectory;
            }
        }

        /// <summary>
        /// Unmounts a previously mounted RAR archive directory.
        /// Also cleans up any symlinks that were created for the mount.
        /// </summary>
        /// <param name="path">Path to the RAR archive file or directory.</param>
        /// <returns>True if unmounted successfully, false otherwise.</returns>
        public bool UnmountArchive(string path)
        {
            var lookupKey = path;
            if (File.Exists(path))
            {
                lookupKey = Path.GetDirectoryName(path) ?? path;
            }

            lock (_lock)
            {
                if (!_mounts.TryGetValue(lookupKey, out var mountInfo))
                {
                    return false;
                }

                // Clean up symlinks first
                foreach (var symlink in mountInfo.CreatedSymlinks)
                {
                    try
                    {
                        if (File.Exists(symlink) || IsSymlink(symlink))
                        {
                            File.Delete(symlink);
                            _logger.LogDebug("Removed symlink: {Path}", symlink);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to remove symlink: {Path}", symlink);
                    }
                }

                if (mountInfo.UsesRar2fs)
                {
                    UnmountWithRar2fs(mountInfo.MountPoint);
                }

                _mounts.Remove(lookupKey);
                return true;
            }
        }

        /// <summary>
        /// Gets the mount point for an archive file or directory.
        /// </summary>
        /// <param name="path">Path to the archive file or directory containing archives.</param>
        /// <returns>Mount point path or null if not mounted.</returns>
        public string? GetMountPoint(string path)
        {
            // Determine if this is a file or directory
            var lookupKey = path;
            if (File.Exists(path))
            {
                lookupKey = Path.GetDirectoryName(path) ?? path;
            }

            lock (_lock)
            {
                if (_mounts.TryGetValue(lookupKey, out var mountInfo))
                {
                    mountInfo.LastAccessed = DateTime.UtcNow;
                    return mountInfo.MountPoint;
                }
                return null;
            }
        }

        /// <summary>
        /// Gets the path to a specific file within a mounted archive.
        /// </summary>
        /// <param name="archivePath">Path to the archive file.</param>
        /// <param name="entryName">Name of the file inside the archive.</param>
        /// <returns>Full path to the file in the mount point, or null if not mounted.</returns>
        public string? GetMountedFilePath(string archivePath, string entryName)
        {
            var mountPoint = GetMountPoint(archivePath);
            if (mountPoint == null)
            {
                return null;
            }

            return Path.Combine(mountPoint, entryName);
        }

        /// <summary>
        /// Gets whether an archive is using rar2fs or fallback streaming.
        /// </summary>
        /// <param name="path">Path to the archive file or directory.</param>
        /// <returns>True if using rar2fs, false if using fallback.</returns>
        public bool IsUsingRar2fs(string path)
        {
            var lookupKey = path;
            if (File.Exists(path))
            {
                lookupKey = Path.GetDirectoryName(path) ?? path;
            }

            lock (_lock)
            {
                return _mounts.TryGetValue(lookupKey, out var mountInfo) && mountInfo.UsesRar2fs;
            }
        }

        /// <summary>
        /// Cleans up unused mounts that haven't been accessed recently.
        /// </summary>
        /// <param name="maxIdleTime">Maximum idle time before unmounting.</param>
        public void CleanupIdleMounts(TimeSpan maxIdleTime)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var toRemove = _mounts
                    .Where(kvp => now - kvp.Value.LastAccessed > maxIdleTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var archivePath in toRemove)
                {
                    _logger.LogInformation("Cleaning up idle mount: {Path}", archivePath);
                    UnmountArchive(archivePath);
                }
            }
        }

        /// <summary>
        /// Unmounts all mounted archives.
        /// </summary>
        public void UnmountAll()
        {
            lock (_lock)
            {
                var archivePaths = _mounts.Keys.ToList();
                foreach (var archivePath in archivePaths)
                {
                    UnmountArchive(archivePath);
                }
            }
        }

        private bool CheckRar2fsAvailability()
        {
            // Only available on Linux/macOS
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _logger.LogInformation("rar2fs is not supported on this platform (Windows)");
                return false;
            }

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = "rar2fs",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    _logger.LogInformation("rar2fs found at: {Path}", output.Trim());
                    return true;
                }

                _logger.LogWarning("rar2fs is not installed");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for rar2fs availability");
                return false;
            }
        }

        /// <summary>
        /// Installs rar2fs from source. This requires build tools and takes 3-5 minutes.
        /// </summary>
        /// <returns>True if installation succeeded, false otherwise.</returns>
        public bool InstallRar2fs()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _logger.LogWarning("rar2fs installation is only supported on Linux");
                return false;
            }

            // Check if already installed
            if (CheckRar2fsAvailability())
            {
                _logger.LogInformation("rar2fs is already installed");
                return true;
            }

            _logger.LogInformation("Installing rar2fs from source (this may take 3-5 minutes)...");

            try
            {
                // Install build dependencies
                if (!RunBashCommand("apt-get update && apt-get install -y --no-install-recommends build-essential autoconf automake libtool libfuse-dev wget ca-certificates"))
                {
                    _logger.LogError("Failed to install build dependencies");
                    return false;
                }

                // Create temp build directory
                var buildDir = Path.Combine(Path.GetTempPath(), $"rar2fs-build-{Guid.NewGuid():N}");
                Directory.CreateDirectory(buildDir);

                try
                {
                    // Download and build unrar library
                    _logger.LogInformation("Downloading unrar source...");
                    if (!RunBashCommand($"cd {buildDir} && wget -q https://www.rarlab.com/rar/unrarsrc-7.1.6.tar.gz && tar xzf unrarsrc-7.1.6.tar.gz"))
                    {
                        _logger.LogError("Failed to download unrar source");
                        return false;
                    }

                    _logger.LogInformation("Building unrar library...");
                    if (!RunBashCommand($"cd {buildDir}/unrar && make -j$(nproc) lib && make install-lib"))
                    {
                        _logger.LogError("Failed to build unrar library");
                        return false;
                    }

                    // Download and build rar2fs
                    _logger.LogInformation("Downloading rar2fs source...");
                    if (!RunBashCommand($"cd {buildDir} && wget -q https://github.com/hasse69/rar2fs/archive/refs/tags/v1.29.7.tar.gz -O rar2fs-1.29.7.tar.gz && tar xzf rar2fs-1.29.7.tar.gz"))
                    {
                        _logger.LogError("Failed to download rar2fs source");
                        return false;
                    }

                    _logger.LogInformation("Building rar2fs...");
                    if (!RunBashCommand($"cd {buildDir}/rar2fs-1.29.7 && autoreconf -i && ./configure --with-unrar={buildDir}/unrar --with-unrar-lib=/usr/lib && make -j$(nproc) && make install"))
                    {
                        _logger.LogError("Failed to build rar2fs");
                        return false;
                    }

                    // Verify installation
                    _rar2fsAvailable = null; // Reset cache
                    if (CheckRar2fsAvailability())
                    {
                        _logger.LogInformation("rar2fs installed successfully");
                        return true;
                    }

                    _logger.LogError("rar2fs installation completed but binary not found");
                    return false;
                }
                finally
                {
                    // Cleanup build directory
                    try
                    {
                        Directory.Delete(buildDir, true);
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error installing rar2fs");
                return false;
            }
        }

        /// <summary>
        /// Resets the rar2fs availability cache, forcing a recheck on next access.
        /// </summary>
        public void ResetAvailabilityCache()
        {
            _rar2fsAvailable = null;
        }

        private bool RunBashCommand(string command)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(600000); // 10 minute timeout for builds

                if (process.ExitCode != 0)
                {
                    _logger.LogError("Command failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running bash command: {Command}", command);
                return false;
            }
        }

        private string? MountWithRar2fs(string sourceDirectory)
        {
            try
            {
                // Mount to UNRAR subfolder within the same directory
                // This keeps content in the library path for natural Jellyfin discovery
                var mountPoint = Path.Combine(sourceDirectory, "UNRAR");

                // Create mount point directory if it doesn't exist
                if (!Directory.Exists(mountPoint))
                {
                    Directory.CreateDirectory(mountPoint);
                }

                _logger.LogDebug("Mounting directory {Source} to {Mount}", sourceDirectory, mountPoint);

                // Mount with rar2fs - it mounts directories containing RAR files
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "rar2fs",
                        Arguments = $"-o allow_other \"{sourceDirectory}\" \"{mountPoint}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000); // Wait up to 5 seconds

                // rar2fs runs in background, so we just check if mount succeeded
                System.Threading.Thread.Sleep(500); // Give it a moment to mount

                if (Directory.Exists(mountPoint) && Directory.GetFileSystemEntries(mountPoint).Length > 0)
                {
                    _logger.LogInformation("Successfully mounted {Source} at {MountPoint}", sourceDirectory, mountPoint);
                    return mountPoint;
                }

                _logger.LogError("Failed to mount with rar2fs. Exit: {Exit}, Output: {Output}, Error: {Error}",
                    process.ExitCode, output, error);

                // Cleanup failed mount point
                try
                {
                    if (Directory.Exists(mountPoint))
                    {
                        Directory.Delete(mountPoint, false);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error mounting with rar2fs: {Path}", sourceDirectory);
                return null;
            }
        }

        private void UnmountWithRar2fs(string mountPoint)
        {
            try
            {
                // Try umount first (works in most containers), then fusermount as fallback
                var success = TryUnmount("umount", $"\"{mountPoint}\"");
                if (!success && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    success = TryUnmount("fusermount", $"-u \"{mountPoint}\"");
                }

                if (!success)
                {
                    _logger.LogWarning("Could not unmount {MountPoint} - may need manual cleanup", mountPoint);
                }

                // Only delete mount point if it's empty AND named "UNRAR"
                // (don't delete temp directories that might have other content)
                try
                {
                    if (Directory.Exists(mountPoint) &&
                        Path.GetFileName(mountPoint) == "UNRAR" &&
                        Directory.GetFileSystemEntries(mountPoint).Length == 0)
                    {
                        Directory.Delete(mountPoint, false);
                        _logger.LogDebug("Removed empty UNRAR directory: {MountPoint}", mountPoint);
                    }
                }
                catch
                {
                    // Ignore cleanup errors - directory may still be mounted
                }

                _logger.LogInformation("Unmounted: {MountPoint}", mountPoint);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unmounting: {MountPoint}", mountPoint);
            }
        }

        private bool TryUnmount(string command, string args)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit(5000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes the manager.
        /// </summary>
        /// <param name="disposing">Whether to dispose managed resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                UnmountAll();
            }

            _disposed = true;
        }

        private class MountInfo
        {
            public string ArchivePath { get; set; } = string.Empty;
            public string MountPoint { get; set; } = string.Empty;
            public DateTime MountedAt { get; set; }
            public DateTime LastAccessed { get; set; }
            public bool UsesRar2fs { get; set; }
            public List<string> CreatedSymlinks { get; set; } = new List<string>();
        }

        /// <summary>
        /// Creates symlinks in the source directory pointing to media files in the mount point.
        /// This makes the mounted content visible to Jellyfin in its library paths.
        /// </summary>
        /// <param name="archivePath">Path to the archive file.</param>
        /// <param name="mediaExtensions">List of media file extensions to symlink (e.g., ".mkv", ".mp4").</param>
        /// <returns>List of created symlink paths.</returns>
        public List<string> CreateSymlinksForMount(string archivePath, IEnumerable<string> mediaExtensions)
        {
            var createdSymlinks = new List<string>();

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _logger.LogDebug("Symlinks not supported on this platform");
                return createdSymlinks;
            }

            var sourceDirectory = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrEmpty(sourceDirectory))
            {
                return createdSymlinks;
            }

            lock (_lock)
            {
                if (!_mounts.TryGetValue(sourceDirectory, out var mountInfo) || !mountInfo.UsesRar2fs)
                {
                    _logger.LogDebug("No rar2fs mount found for {Path}", archivePath);
                    return createdSymlinks;
                }

                var mountPoint = mountInfo.MountPoint;
                var extensionSet = new HashSet<string>(
                    mediaExtensions.Select(e => e.ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase);

                try
                {
                    // Find all media files in the mount point
                    var mediaFiles = Directory.EnumerateFiles(mountPoint, "*", SearchOption.AllDirectories)
                        .Where(f => extensionSet.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .ToList();

                    foreach (var mountedFile in mediaFiles)
                    {
                        try
                        {
                            // Calculate relative path from mount point
                            var relativePath = Path.GetRelativePath(mountPoint, mountedFile);
                            var symlinkPath = Path.Combine(sourceDirectory, relativePath);

                            // Skip if file/symlink already exists
                            if (File.Exists(symlinkPath))
                            {
                                _logger.LogDebug("File already exists at symlink target: {Path}", symlinkPath);
                                continue;
                            }

                            // Ensure parent directory exists
                            var symlinkDir = Path.GetDirectoryName(symlinkPath);
                            if (!string.IsNullOrEmpty(symlinkDir) && !Directory.Exists(symlinkDir))
                            {
                                Directory.CreateDirectory(symlinkDir);
                            }

                            // Create symlink using ln -s
                            if (CreateSymlink(mountedFile, symlinkPath))
                            {
                                createdSymlinks.Add(symlinkPath);
                                mountInfo.CreatedSymlinks.Add(symlinkPath);
                                _logger.LogInformation("Created symlink: {Symlink} -> {Target}", symlinkPath, mountedFile);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to create symlink for {File}", mountedFile);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating symlinks for mount {Mount}", mountPoint);
                }
            }

            return createdSymlinks;
        }

        /// <summary>
        /// Removes all symlinks created for a mount.
        /// </summary>
        /// <param name="archivePath">Path to the archive file.</param>
        public void RemoveSymlinksForMount(string archivePath)
        {
            var sourceDirectory = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrEmpty(sourceDirectory))
            {
                return;
            }

            lock (_lock)
            {
                if (!_mounts.TryGetValue(sourceDirectory, out var mountInfo))
                {
                    return;
                }

                foreach (var symlink in mountInfo.CreatedSymlinks.ToList())
                {
                    try
                    {
                        if (File.Exists(symlink) || IsSymlink(symlink))
                        {
                            File.Delete(symlink);
                            _logger.LogDebug("Removed symlink: {Path}", symlink);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to remove symlink: {Path}", symlink);
                    }
                }

                mountInfo.CreatedSymlinks.Clear();
            }
        }

        /// <summary>
        /// Gets the list of symlinks created for a mount.
        /// </summary>
        /// <param name="archivePath">Path to the archive file.</param>
        /// <returns>List of symlink paths.</returns>
        public IReadOnlyList<string> GetSymlinksForMount(string archivePath)
        {
            var sourceDirectory = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrEmpty(sourceDirectory))
            {
                return Array.Empty<string>();
            }

            lock (_lock)
            {
                if (_mounts.TryGetValue(sourceDirectory, out var mountInfo))
                {
                    return mountInfo.CreatedSymlinks.ToList();
                }
                return Array.Empty<string>();
            }
        }

        private bool CreateSymlink(string target, string symlink)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ln",
                        Arguments = $"-s \"{target}\" \"{symlink}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit(5000);
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create symlink {Symlink} -> {Target}", symlink, target);
                return false;
            }
        }

        private bool IsSymlink(string path)
        {
            try
            {
                var fileInfo = new FileInfo(path);
                return fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return false;
            }
        }
    }
}
