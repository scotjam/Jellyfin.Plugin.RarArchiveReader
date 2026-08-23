using System;
using System.IO;
using SharpCompress.Archives.Rar;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// A buffered stream that supports seeking by re-reading from the RAR archive.
    /// Uses chunked buffering to minimize memory usage while still supporting seeks.
    /// </summary>
    public class RarBufferedStream : Stream
    {
        /// <summary>
        /// Default buffer size in MB.
        /// </summary>
        public const int DefaultBufferSizeMB = 264;

        private readonly string _archivePath;
        private readonly string _entryKey;
        private readonly long _length;
        private readonly int _bufferSize;

        private byte[] _buffer;
        private long _bufferStart;  // Position in the file where buffer starts
        private int _bufferLength;  // How much valid data is in the buffer
        private long _position;     // Current read position in the file

        private RarArchive? _archive;
        private Stream? _entryStream;
        private long _streamPosition; // Current position in the decompression stream

        /// <summary>
        /// Creates a new buffered stream for a RAR archive entry.
        /// </summary>
        /// <param name="archivePath">Path to the RAR archive.</param>
        /// <param name="entryKey">Key of the entry within the archive.</param>
        /// <param name="length">Total length of the entry.</param>
        /// <param name="bufferSizeMB">Buffer size in megabytes (default 264MB).</param>
        public RarBufferedStream(string archivePath, string entryKey, long length, int bufferSizeMB = DefaultBufferSizeMB)
        {
            _archivePath = archivePath;
            _entryKey = entryKey;
            _length = length;
            _bufferSize = bufferSizeMB * 1024 * 1024;

            _buffer = new byte[_bufferSize];
            _bufferStart = 0;
            _bufferLength = 0;
            _position = 0;
            _streamPosition = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length)
                return 0;

            int totalRead = 0;

            while (count > 0 && _position < _length)
            {
                // Check if we need to fill the buffer
                if (!IsPositionInBuffer(_position))
                {
                    FillBufferAt(_position);
                }

                // Calculate how much we can read from the buffer
                int bufferOffset = (int)(_position - _bufferStart);
                int available = _bufferLength - bufferOffset;
                int toRead = Math.Min(count, available);

                if (toRead <= 0)
                    break;

                // Copy from buffer to output
                Array.Copy(_buffer, bufferOffset, buffer, offset, toRead);

                _position += toRead;
                offset += toRead;
                count -= toRead;
                totalRead += toRead;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
            };

            if (newPosition < 0)
                newPosition = 0;
            if (newPosition > _length)
                newPosition = _length;

            _position = newPosition;
            return _position;
        }

        private bool IsPositionInBuffer(long position)
        {
            return position >= _bufferStart &&
                   position < _bufferStart + _bufferLength &&
                   _bufferLength > 0;
        }

        private void FillBufferAt(long position)
        {
            // If we need to go backward or far forward, reopen the stream
            if (_entryStream == null || position < _streamPosition || position > _streamPosition + _bufferSize * 2)
            {
                CloseEntryStream();
                OpenEntryStream();
                _streamPosition = 0;
            }

            // Skip forward to the desired position
            byte[] skipBuffer = new byte[81920]; // 80KB skip buffer
            while (_streamPosition < position)
            {
                int toSkip = (int)Math.Min(position - _streamPosition, skipBuffer.Length);
                int skipped = _entryStream!.Read(skipBuffer, 0, toSkip);
                if (skipped == 0)
                    break;
                _streamPosition += skipped;
            }

            // Now fill the buffer
            _bufferStart = _streamPosition;
            _bufferLength = 0;

            while (_bufferLength < _bufferSize)
            {
                int toRead = Math.Min(_bufferSize - _bufferLength, 81920);
                int read = _entryStream!.Read(_buffer, _bufferLength, toRead);
                if (read == 0)
                    break;
                _bufferLength += read;
                _streamPosition += read;
            }
        }

        private void OpenEntryStream()
        {
            _archive = RarArchive.Open(_archivePath);
            foreach (var entry in _archive.Entries)
            {
                if (entry.Key == _entryKey && !entry.IsDirectory)
                {
                    _entryStream = entry.OpenEntryStream();
                    return;
                }
            }
            throw new InvalidOperationException($"Entry not found: {_entryKey}");
        }

        private void CloseEntryStream()
        {
            // SharpCompress entry streams can throw on Dispose when abandoned mid-read
            // (e.g. the viewer stops playback). If that exception escaped here the
            // archive below would never be disposed, leaking one file handle per RAR
            // volume for every aborted playback session.
            try
            {
                _entryStream?.Dispose();
            }
            catch (Exception)
            {
                // Ignore - releasing the archive below closes the underlying files.
            }

            _entryStream = null;

            try
            {
                _archive?.Dispose();
            }
            catch (Exception)
            {
                // Ignore - nothing more we can do; the FileStreams are finalizable.
            }

            _archive = null;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseEntryStream();
                _buffer = Array.Empty<byte>();
            }
            base.Dispose(disposing);
        }
    }
}
