using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Provides functionality to read RAR archives in memory.
    /// </summary>
    public class RarArchiveReader : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _archivePath;
        private RarArchive? _archive;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="RarArchiveReader"/> class.
        /// </summary>
        /// <param name="archivePath">Path to the RAR archive.</param>
        /// <param name="logger">Logger instance.</param>
        public RarArchiveReader(string archivePath, ILogger logger)
        {
            _archivePath = archivePath ?? throw new ArgumentNullException(nameof(archivePath));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Opens the RAR archive.
        /// </summary>
        /// <returns>True if successful, false otherwise.</returns>
        public bool Open()
        {
            try
            {
                if (_archive != null)
                {
                    return true;
                }

                if (!File.Exists(_archivePath))
                {
                    _logger.LogError("Archive file not found: {Path}", _archivePath);
                    return false;
                }

                // SharpCompress finds the remaining volumes from the first one
                var archive = RarArchive.Open(_archivePath);

                // Only publish the archive once every volume is readable, so a failed Open() can be retried
                int volumeCount;
                try
                {
                    volumeCount = archive.Volumes.Count();
                }
                catch
                {
                    archive.Dispose();
                    throw;
                }

                _archive = archive;
                if (volumeCount > 1)
                {
                    _logger.LogInformation("Opened multi-part RAR archive with {Count} volumes: {Path}", volumeCount, _archivePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open RAR archive: {Path}", _archivePath);
                return false;
            }
        }

        /// <summary>
        /// Gets a list of all entries in the archive.
        /// </summary>
        /// <returns>List of archive entry information.</returns>
        public List<ArchiveEntryInfo> GetEntries()
        {
            if (_archive == null)
            {
                if (!Open())
                {
                    return new List<ArchiveEntryInfo>();
                }
            }

            var entries = new List<ArchiveEntryInfo>();

            try
            {
                foreach (var entry in _archive!.Entries.Where(e => !e.IsDirectory))
                {
                    entries.Add(new ArchiveEntryInfo
                    {
                        Key = entry.Key,
                        Size = entry.Size,
                        CompressedSize = entry.CompressedSize,
                        CreatedTime = entry.CreatedTime,
                        LastModifiedTime = entry.LastModifiedTime,
                        IsEncrypted = entry.IsEncrypted
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read archive entries from: {Path}", _archivePath);
            }

            return entries;
        }

        /// <summary>
        /// Extracts a specific file from the archive to a stream.
        /// </summary>
        /// <param name="entryPath">Path of the entry within the archive.</param>
        /// <param name="outputStream">Output stream to write to.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        public async Task<bool> ExtractToStreamAsync(string entryPath, Stream outputStream, CancellationToken cancellationToken = default)
        {
            if (_archive == null)
            {
                if (!Open())
                {
                    return false;
                }
            }

            try
            {
                var entry = _archive!.Entries.FirstOrDefault(e => e.Key == entryPath);
                if (entry == null || entry.IsDirectory)
                {
                    _logger.LogWarning("Entry not found or is a directory: {Path}", entryPath);
                    return false;
                }

                if (entry.IsEncrypted)
                {
                    _logger.LogWarning("Entry is encrypted and cannot be extracted: {Path}", entryPath);
                    return false;
                }

                using (var entryStream = entry.OpenEntryStream())
                {
                    await entryStream.CopyToAsync(outputStream, 81920, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Extract operation cancelled for: {Path}", entryPath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract entry: {Path}", entryPath);
                return false;
            }
        }

        /// <summary>
        /// Gets a stream for reading a specific file from the archive.
        /// </summary>
        /// <param name="entryPath">Path of the entry within the archive.</param>
        /// <returns>Stream for reading the entry, or null if not found.</returns>
        public Stream? GetEntryStream(string entryPath)
        {
            if (_archive == null)
            {
                if (!Open())
                {
                    return null;
                }
            }

            try
            {
                var entry = _archive!.Entries.FirstOrDefault(e => e.Key == entryPath);
                if (entry == null || entry.IsDirectory || entry.IsEncrypted)
                {
                    return null;
                }

                // Return the entry stream directly for memory-efficient streaming
                // Note: This stream may not support seeking, but avoids loading entire file into memory
                return entry.OpenEntryStream();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get entry stream: {Path}", entryPath);
                return null;
            }
        }

        /// <summary>
        /// Tries to build a volume/offset map for direct seekable access to a stored entry.
        /// Returns null when the entry is compressed, encrypted, or the map cannot be built.
        /// </summary>
        /// <param name="entryPath">Path of the entry within the archive.</param>
        /// <returns>Ordered segments, or null.</returns>
        public IReadOnlyList<StoredRarSegment>? TryGetStoredSegments(string entryPath)
        {
            if (_archive == null)
            {
                if (!Open())
                {
                    return null;
                }
            }

            return StoredRarMap.TryBuild(_archive!, entryPath, _logger);
        }

        /// <summary>
        /// Checks if a specific entry exists in the archive.
        /// </summary>
        /// <param name="entryPath">Path of the entry within the archive.</param>
        /// <returns>True if the entry exists, false otherwise.</returns>
        public bool EntryExists(string entryPath)
        {
            if (_archive == null)
            {
                if (!Open())
                {
                    return false;
                }
            }

            return _archive!.Entries.Any(e => e.Key == entryPath && !e.IsDirectory);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes the archive reader.
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
                _archive?.Dispose();
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Information about an entry in an archive.
    /// </summary>
    public class ArchiveEntryInfo
    {
        /// <summary>
        /// Gets or sets the key (path) of the entry.
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the uncompressed size.
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Gets or sets the compressed size.
        /// </summary>
        public long CompressedSize { get; set; }

        /// <summary>
        /// Gets or sets the creation time.
        /// </summary>
        public DateTime? CreatedTime { get; set; }

        /// <summary>
        /// Gets or sets the last modified time.
        /// </summary>
        public DateTime? LastModifiedTime { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the entry is encrypted.
        /// </summary>
        public bool IsEncrypted { get; set; }
    }
}
