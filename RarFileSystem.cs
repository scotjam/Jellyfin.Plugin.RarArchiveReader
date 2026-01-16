using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            lock (_lock)
            {
                if (_openArchives.TryGetValue(archivePath, out var existingReader))
                {
                    return existingReader;
                }

                var reader = new RarArchiveReader(archivePath, _logger);
                if (reader.Open())
                {
                    _openArchives[archivePath] = reader;
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
