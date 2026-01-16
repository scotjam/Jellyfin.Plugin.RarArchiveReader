using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Provides media streams from RAR archives.
    /// </summary>
    public class RarMediaStreamProvider
    {
        private readonly ILogger _logger;
        private readonly RarFileSystem _fileSystem;

        /// <summary>
        /// Initializes a new instance of the <see cref="RarMediaStreamProvider"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="fileSystem">RAR file system instance.</param>
        public RarMediaStreamProvider(ILogger logger, RarFileSystem fileSystem)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        }

        /// <summary>
        /// Gets a stream for a file in an archive.
        /// </summary>
        /// <param name="path">Virtual path to the file.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Stream for reading the file.</returns>
        public Task<Stream?> GetStreamAsync(string path, CancellationToken cancellationToken = default)
        {
            try
            {
                var stream = _fileSystem.GetStream(path);
                return Task.FromResult(stream);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get stream for: {Path}", path);
                return Task.FromResult<Stream?>(null);
            }
        }

        /// <summary>
        /// Checks if a path is a virtual archive path.
        /// </summary>
        /// <param name="path">Path to check.</param>
        /// <returns>True if the path is a virtual archive path.</returns>
        public bool IsVirtualPath(string path)
        {
            return RarFileSystem.ParseVirtualPath(path).HasValue;
        }

        /// <summary>
        /// Gets information about a file in an archive.
        /// </summary>
        /// <param name="path">Virtual path to the file.</param>
        /// <returns>File information, or null if not found.</returns>
        public FileSystemMetadata? GetFileInfo(string path)
        {
            var parsed = RarFileSystem.ParseVirtualPath(path);
            if (!parsed.HasValue)
            {
                return null;
            }

            var (archivePath, entryPath) = parsed.Value;

            if (!File.Exists(archivePath))
            {
                return null;
            }

            if (string.IsNullOrEmpty(entryPath))
            {
                // Return info about the archive itself
                var fileInfo = new FileInfo(archivePath);
                return new FileSystemMetadata
                {
                    FullName = archivePath,
                    Name = fileInfo.Name,
                    Length = fileInfo.Length,
                    LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
                    CreationTimeUtc = fileInfo.CreationTimeUtc,
                    IsDirectory = false,
                    Exists = true
                };
            }

            // Get info about the entry
            var reader = _fileSystem.GetArchiveReader(archivePath);
            if (reader == null)
            {
                return null;
            }

            var entries = reader.GetEntries();
            var entry = entries.Find(e => e.Key == entryPath);
            if (entry == null)
            {
                return null;
            }

            // Use the archive file's timestamps instead of the entry's timestamps
            // This is important because timestamps within archives can't be updated after creation
            var archiveFileInfo = new FileInfo(archivePath);

            return new FileSystemMetadata
            {
                FullName = path,
                Name = Path.GetFileName(entryPath),
                Length = entry.Size,
                LastWriteTimeUtc = archiveFileInfo.LastWriteTimeUtc,
                CreationTimeUtc = archiveFileInfo.CreationTimeUtc,
                IsDirectory = false,
                Exists = true
            };
        }
    }

    /// <summary>
    /// Custom stream wrapper that supports seeking for RAR archive entries.
    /// </summary>
    public class RarEntryStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _length;
        private readonly bool _canSeek;

        /// <summary>
        /// Initializes a new instance of the <see cref="RarEntryStream"/> class.
        /// </summary>
        /// <param name="baseStream">The base stream from the archive entry.</param>
        /// <param name="length">The length of the entry.</param>
        public RarEntryStream(Stream baseStream, long length)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
            _length = length;
            _canSeek = baseStream.CanSeek;
        }

        /// <inheritdoc />
        public override bool CanRead => _baseStream.CanRead;

        /// <inheritdoc />
        public override bool CanSeek => _canSeek;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _length;

        /// <inheritdoc />
        public override long Position
        {
            get => _baseStream.Position;
            set
            {
                if (!_canSeek)
                {
                    throw new NotSupportedException("Stream does not support seeking");
                }
                _baseStream.Position = value;
            }
        }

        /// <inheritdoc />
        public override void Flush()
        {
            _baseStream.Flush();
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            return _baseStream.Read(buffer, offset, count);
        }

        /// <inheritdoc />
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return await _baseStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!_canSeek)
            {
                throw new NotSupportedException("Stream does not support seeking");
            }
            return _baseStream.Seek(offset, origin);
        }

        /// <inheritdoc />
        public override void SetLength(long value)
        {
            throw new NotSupportedException("Stream does not support setting length");
        }

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("Stream does not support writing");
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _baseStream?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
