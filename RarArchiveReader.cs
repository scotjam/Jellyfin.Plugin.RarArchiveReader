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

                // Check for multi-part RAR archive and get all parts
                var archiveParts = GetMultiPartArchivePaths(_archivePath);

                if (archiveParts.Count > 1)
                {
                    _logger.LogInformation("Opening multi-part RAR archive with {Count} parts: {Path}", archiveParts.Count, _archivePath);
                }

                // Open the archive (SharpCompress automatically handles multi-part archives)
                _archive = RarArchive.Open(_archivePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open RAR archive: {Path}", _archivePath);
                return false;
            }
        }

        /// <summary>
        /// Gets all parts of a multi-part RAR archive.
        /// </summary>
        /// <param name="firstPartPath">Path to the first part (or any part) of the archive.</param>
        /// <returns>List of all archive part paths.</returns>
        private List<string> GetMultiPartArchivePaths(string firstPartPath)
        {
            var parts = new List<string>();
            var directory = Path.GetDirectoryName(firstPartPath);
            var fileName = Path.GetFileNameWithoutExtension(firstPartPath);
            var extension = Path.GetExtension(firstPartPath).ToLowerInvariant();

            if (string.IsNullOrEmpty(directory))
            {
                parts.Add(firstPartPath);
                return parts;
            }

            // Handle different multi-part naming conventions:
            // 1. .rar, .r00, .r01, .r02, etc.
            // 2. .part1.rar, .part2.rar, .part3.rar, etc.
            // 3. .part01.rar, .part02.rar, .part03.rar, etc.

            if (extension == ".rar")
            {
                // Check for .r00, .r01 style
                parts.Add(firstPartPath);
                for (int i = 0; i < 1000; i++)
                {
                    var partPath = Path.Combine(directory, $"{fileName}.r{i:D2}");
                    if (File.Exists(partPath))
                    {
                        parts.Add(partPath);
                    }
                    else
                    {
                        break;
                    }
                }

                // Check for .part1.rar style
                if (fileName.Contains(".part"))
                {
                    var baseName = fileName.Substring(0, fileName.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase));
                    for (int i = 1; i < 1000; i++)
                    {
                        var partPath = Path.Combine(directory, $"{baseName}.part{i}.rar");
                        if (File.Exists(partPath))
                        {
                            if (!parts.Contains(partPath))
                            {
                                parts.Add(partPath);
                            }
                        }
                        else
                        {
                            // Try zero-padded version
                            partPath = Path.Combine(directory, $"{baseName}.part{i:D2}.rar");
                            if (File.Exists(partPath))
                            {
                                if (!parts.Contains(partPath))
                                {
                                    parts.Add(partPath);
                                }
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }
            }

            return parts.Count > 0 ? parts : new List<string> { firstPartPath };
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

                // For streaming, we need to copy to a memory stream
                // because SharpCompress entry streams can't always seek
                var memoryStream = new MemoryStream();
                using (var entryStream = entry.OpenEntryStream())
                {
                    entryStream.CopyTo(memoryStream);
                }

                memoryStream.Position = 0;
                return memoryStream;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get entry stream: {Path}", entryPath);
                return null;
            }
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
