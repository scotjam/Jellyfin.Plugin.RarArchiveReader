using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Provides a virtual file system for RAR archives.
    /// </summary>
    public class RarFileSystem : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, RarArchiveReader> _openArchives;
        private readonly Dictionary<string, (int Count, long TotalLength, long MaxTicks)> _cacheStamps;
        private readonly object _lock = new object();
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="RarFileSystem"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        public RarFileSystem(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _openArchives = new Dictionary<string, RarArchiveReader>();
            _cacheStamps = new Dictionary<string, (int, long, long)>();
        }

        /// <summary>
        /// Computes a change-detection stamp over every RAR volume in the archive's
        /// directory (count, total bytes, newest write time). A per-release folder
        /// holds exactly one volume set, so any add/remove/rewrite of a part — including
        /// the later <c>.r00..rNN</c> parts that hardlink shuffles touch while the first
        /// <c>.rar</c> volume keeps its old mtime — changes the stamp and forces a reopen.
        /// </summary>
        /// <param name="archivePath">Path to any volume of the archive (the first volume is passed in practice).</param>
        /// <returns>A tuple uniquely identifying the current on-disk state of the volume set.</returns>
        private static (int Count, long TotalLength, long MaxTicks) ComputeVolumeStamp(string archivePath)
        {
            var dir = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrEmpty(dir))
            {
                return (0, 0, 0);
            }

            int count = 0;
            long total = 0;
            long maxTicks = 0;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    var isVolume = ext == ".rar"
                        || ext == ".cbr"
                        || System.Text.RegularExpressions.Regex.IsMatch(ext, @"^\.r\d{2,3}$");

                    if (!isVolume)
                    {
                        continue;
                    }

                    var info = new FileInfo(file);
                    count++;
                    total += info.Length;
                    var ticks = info.LastWriteTimeUtc.Ticks;
                    if (ticks > maxTicks)
                    {
                        maxTicks = ticks;
                    }
                }
            }
            catch (Exception)
            {
                // If the directory can't be enumerated, fall back to a zero stamp so the
                // caller treats the cache as stale and reopens (fail-safe, never fail-stale).
                return (0, 0, 0);
            }

            return (count, total, maxTicks);
        }

        /// <summary>
        /// Checks if a path points to a RAR archive.
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <returns>True if the path is a RAR archive.</returns>
        public static bool IsRarArchive(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".rar" || extension == ".cbr";
        }

        /// <summary>
        /// Recursively finds RAR files under a root path, skipping UNRAR mount point
        /// directories to avoid traversing slow FUSE mounts.
        /// </summary>
        /// <param name="rootPath">The root directory to scan.</param>
        /// <param name="deniedPaths">Collects paths where access was denied.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>List of RAR file paths found.</returns>
        public static List<string> FindRarFiles(string rootPath, List<string> deniedPaths, CancellationToken cancellationToken)
        {
            var results = new List<string>();
            var stack = new Stack<string>();
            stack.Push(rootPath);

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = stack.Pop();

                // Enumerate files in this directory
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.rar"))
                    {
                        if (IsRarArchive(file))
                        {
                            results.Add(file);
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    deniedPaths.Add(dir);
                    continue;
                }
                catch (SecurityException)
                {
                    deniedPaths.Add(dir);
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                // Enumerate subdirectories, skipping UNRAR mount points
                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(dir))
                    {
                        if (string.Equals(Path.GetFileName(subDir), "UNRAR", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        stack.Push(subDir);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    deniedPaths.Add(dir);
                }
                catch (SecurityException)
                {
                    deniedPaths.Add(dir);
                }
                catch (DirectoryNotFoundException)
                {
                    // Directory was removed between enumeration and access
                }
            }

            return results;
        }

        /// <summary>
        /// Logs a warning with a copy-pasteable shell command to fix permission-denied paths.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="deniedPaths">List of paths that were inaccessible.</param>
        public static void LogDeniedPaths(ILogger logger, List<string> deniedPaths)
        {
            if (deniedPaths.Count == 0)
            {
                return;
            }

            var uniquePaths = deniedPaths.Distinct().ToList();
            var pathList = string.Join("\n  ", uniquePaths);
            var chownCommands = string.Join("\n", uniquePaths.Select(p => $"  chown -R 1000:100 \"{p}\""));

            logger.LogWarning(
                "Permission denied for {Count} path(s). The Jellyfin user cannot access these directories:\n  {Paths}\n\n" +
                "To fix, run the following commands in your Docker host shell:\n{Commands}\n\n" +
                "Or if running Jellyfin natively, replace 1000:100 with your Jellyfin user/group.",
                uniquePaths.Count,
                pathList,
                chownCommands);
        }

        /// <summary>
        /// Parses a virtual path that includes archive and entry components.
        /// </summary>
        /// <param name="virtualPath">The virtual path (e.g., "archive.rar/video.mkv").</param>
        /// <returns>Tuple of archive path and entry path, or null if invalid.</returns>
        public static (string ArchivePath, string EntryPath)? ParseVirtualPath(string virtualPath)
        {
            if (string.IsNullOrEmpty(virtualPath))
            {
                return null;
            }

            // Look for .rar or .cbr in the path
            var rarIndex = virtualPath.LastIndexOf(".rar", StringComparison.OrdinalIgnoreCase);
            var cbrIndex = virtualPath.LastIndexOf(".cbr", StringComparison.OrdinalIgnoreCase);

            var archiveEndIndex = Math.Max(rarIndex, cbrIndex);
            if (archiveEndIndex < 0)
            {
                return null;
            }

            archiveEndIndex += 4; // Length of ".rar" or ".cbr"

            var archivePath = virtualPath.Substring(0, archiveEndIndex);

            if (archiveEndIndex >= virtualPath.Length)
            {
                return (archivePath, string.Empty);
            }

            // Skip path separator if present
            var entryStartIndex = archiveEndIndex;
            if (virtualPath[entryStartIndex] == Path.DirectorySeparatorChar ||
                virtualPath[entryStartIndex] == Path.AltDirectorySeparatorChar)
            {
                entryStartIndex++;
            }

            var entryPath = virtualPath.Substring(entryStartIndex);
            return (archivePath, entryPath);
        }

        /// <summary>
        /// Gets or opens a RAR archive reader.
        /// </summary>
        /// <param name="archivePath">Path to the archive.</param>
        /// <returns>Archive reader instance, or null if failed.</returns>
        public RarArchiveReader? GetArchiveReader(string archivePath)
        {
            if (!File.Exists(archivePath))
            {
                return null;
            }

            var stamp = ComputeVolumeStamp(archivePath);

            lock (_lock)
            {
                if (_openArchives.TryGetValue(archivePath, out var existingReader))
                {
                    // Reuse the cached reader only if the volume set is byte-for-byte the
                    // same as when it was opened. Without this check a reader cached during
                    // an incomplete or since-replaced state stays broken for the whole
                    // Jellyfin process lifetime (the cause of archives silently reporting
                    // "no media" after a hardlink/re-download under a long-running server).
                    if (_cacheStamps.TryGetValue(archivePath, out var cachedStamp) && cachedStamp == stamp)
                    {
                        return existingReader;
                    }

                    _logger.LogInformation("RAR volume set changed on disk, reopening archive: {Path}", archivePath);
                    existingReader.Dispose();
                    _openArchives.Remove(archivePath);
                    _cacheStamps.Remove(archivePath);
                }

                var reader = new RarArchiveReader(archivePath, _logger);
                if (reader.Open())
                {
                    _openArchives[archivePath] = reader;
                    _cacheStamps[archivePath] = stamp;
                    return reader;
                }

                reader.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Gets a stream for reading a file from an archive.
        /// </summary>
        /// <param name="virtualPath">Virtual path including archive and entry.</param>
        /// <returns>Stream for reading, or null if not found.</returns>
        public Stream? GetStream(string virtualPath)
        {
            var parsed = ParseVirtualPath(virtualPath);
            if (!parsed.HasValue)
            {
                return null;
            }

            var (archivePath, entryPath) = parsed.Value;
            var reader = GetArchiveReader(archivePath);
            if (reader == null)
            {
                return null;
            }

            return reader.GetEntryStream(entryPath);
        }

        /// <summary>
        /// Gets information about entries in an archive.
        /// </summary>
        /// <param name="archivePath">Path to the archive.</param>
        /// <returns>List of entry information.</returns>
        public List<ArchiveEntryInfo> GetArchiveEntries(string archivePath)
        {
            var reader = GetArchiveReader(archivePath);
            if (reader == null)
            {
                return new List<ArchiveEntryInfo>();
            }

            return reader.GetEntries();
        }

        /// <summary>
        /// Checks if a virtual path exists.
        /// </summary>
        /// <param name="virtualPath">The virtual path to check.</param>
        /// <returns>True if the path exists.</returns>
        public bool Exists(string virtualPath)
        {
            var parsed = ParseVirtualPath(virtualPath);
            if (!parsed.HasValue)
            {
                return false;
            }

            var (archivePath, entryPath) = parsed.Value;

            if (!File.Exists(archivePath))
            {
                return false;
            }

            if (string.IsNullOrEmpty(entryPath))
            {
                return true; // Just checking if archive exists
            }

            var reader = GetArchiveReader(archivePath);
            return reader?.EntryExists(entryPath) ?? false;
        }

        /// <summary>
        /// Closes an archive and removes it from the cache.
        /// </summary>
        /// <param name="archivePath">Path to the archive to close.</param>
        public void CloseArchive(string archivePath)
        {
            lock (_lock)
            {
                if (_openArchives.TryGetValue(archivePath, out var reader))
                {
                    reader.Dispose();
                    _openArchives.Remove(archivePath);
                    _cacheStamps.Remove(archivePath);
                }
            }
        }

        /// <summary>
        /// Closes all open archives.
        /// </summary>
        public void CloseAll()
        {
            lock (_lock)
            {
                foreach (var reader in _openArchives.Values)
                {
                    reader.Dispose();
                }
                _openArchives.Clear();
                _cacheStamps.Clear();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes the file system.
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
                CloseAll();
            }

            _disposed = true;
        }
    }
}
