using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives.Rar;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// One contiguous run of a stored (uncompressed) archive entry inside a single RAR volume file.
    /// </summary>
    /// <param name="VolumePath">Path of the volume file on disk.</param>
    /// <param name="DataStart">Byte offset of this run inside the volume file.</param>
    /// <param name="Length">Number of bytes of the entry stored in this run.</param>
    public sealed record StoredRarSegment(string VolumePath, long DataStart, long Length);

    /// <summary>
    /// A fully seekable read-only <see cref="Stream"/> over a stored (compression method m0) RAR entry.
    /// <para>
    /// Stored entries are raw file bytes written at known offsets across the volume files, so any
    /// position can be served by seeking directly into the right volume — no decompression pass.
    /// This is what makes instant seeking possible: video players fetch the index at the END of a
    /// Matroska file before showing the first frame, which previously forced a sequential read of
    /// the entire archive.
    /// </para>
    /// </summary>
    public sealed class StoredRarStream : Stream
    {
        private readonly IReadOnlyList<StoredRarSegment> _segments;
        private readonly long[] _segmentStarts; // start offset of each segment within the entry
        private readonly long _length;

        private long _position;
        private int _currentIndex = -1;
        private FileStream? _current;

        /// <summary>
        /// Initializes a new instance of the <see cref="StoredRarStream"/> class.
        /// </summary>
        /// <param name="segments">Ordered volume runs covering the whole entry.</param>
        public StoredRarStream(IReadOnlyList<StoredRarSegment> segments)
        {
            _segments = segments;
            _segmentStarts = new long[segments.Count];
            long total = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                _segmentStarts[i] = total;
                total += segments[i].Length;
            }

            _length = total;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => true;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _length;

        /// <inheritdoc />
        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, _length);
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentException("Invalid seek origin", nameof(origin)),
            };

            _position = Math.Clamp(target, 0, _length);
            return _position;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;

            while (count > 0 && _position < _length)
            {
                // Locate the segment containing _position.
                int idx = FindSegment(_position);
                var segment = _segments[idx];
                long offsetInSegment = _position - _segmentStarts[idx];
                long remainingInSegment = segment.Length - offsetInSegment;

                if (_currentIndex != idx)
                {
                    _current?.Dispose();
                    _current = new FileStream(
                        segment.VolumePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Write | FileShare.Delete,
                        bufferSize: 128 * 1024);
                    _currentIndex = idx;
                }

                _current!.Position = segment.DataStart + offsetInSegment;

                int toRead = (int)Math.Min(count, remainingInSegment);
                int read = _current.Read(buffer, offset, toRead);
                if (read <= 0)
                {
                    break; // truncated volume on disk; report what we have
                }

                _position += read;
                offset += read;
                count -= read;
                totalRead += read;
            }

            return totalRead;
        }

        private int FindSegment(long position)
        {
            // Fast path: sequential reads stay in (or move to) the current/next segment.
            if (_currentIndex >= 0)
            {
                if (Contains(_currentIndex, position))
                {
                    return _currentIndex;
                }

                if (_currentIndex + 1 < _segments.Count && Contains(_currentIndex + 1, position))
                {
                    return _currentIndex + 1;
                }
            }

            int lo = 0, hi = _segments.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_segmentStarts[mid] <= position)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return lo;
        }

        private bool Contains(int idx, long position)
            => position >= _segmentStarts[idx] && position < _segmentStarts[idx] + _segments[idx].Length;

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _current?.Dispose();
                _current = null;
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Builds the volume/offset map for a stored RAR entry using SharpCompress metadata.
    /// Uses reflection into SharpCompress internals (parts, FileHeader, volume stream); any
    /// mismatch (library update, compressed entry, encryption) simply returns null and the
    /// caller falls back to the sequential <see cref="RarBufferedStream"/>.
    /// </summary>
    public static class StoredRarMap
    {
        private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Tries to build the ordered segment list for a stored, unencrypted entry.
        /// </summary>
        /// <param name="archive">An open SharpCompress archive.</param>
        /// <param name="entryKey">Entry key inside the archive.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <returns>The segments, or null when direct access is not possible.</returns>
        public static IReadOnlyList<StoredRarSegment>? TryBuild(RarArchive archive, string entryKey, ILogger logger)
        {
            try
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Key == entryKey && !e.IsDirectory);
                if (entry is null)
                {
                    return Fail(logger, entryKey, "entry not found");
                }

                if (entry.IsEncrypted)
                {
                    return Fail(logger, entryKey, "entry is encrypted");
                }

                // Ordered volume file list backing this archive (SourceStream.Files is public;
                // only the _sourceStream field itself needs reflection).
                var sourceStreamField = FindField(archive.GetType(), "_sourceStream");
                var sourceStream = sourceStreamField?.GetValue(archive);
                var volumeFiles = (sourceStream?.GetType().GetProperty("Files", AnyInstance)?.GetValue(sourceStream)
                    as System.Collections.Generic.IEnumerable<FileInfo>)?.ToList();
                if (volumeFiles is null || volumeFiles.Count == 0)
                {
                    return Fail(logger, entryKey, "could not resolve volume file list");
                }

                var partsField = FindField(typeof(RarArchiveEntry), "parts");
                if (partsField?.GetValue(entry) is not System.Collections.IEnumerable partsEnumerable)
                {
                    return Fail(logger, entryKey, "could not read entry parts");
                }

                var segments = new List<StoredRarSegment>();
                long totalStored = 0;

                foreach (var part in partsEnumerable)
                {
                    if (part is not SharpCompress.Common.FilePart filePart)
                    {
                        return Fail(logger, entryKey, "unexpected part type " + part?.GetType().Name);
                    }

                    var fileHeader = FindProperty(part.GetType(), "FileHeader")?.GetValue(part);
                    if (fileHeader is null)
                    {
                        return Fail(logger, entryKey, "could not read part FileHeader");
                    }

                    var fhType = fileHeader.GetType();

                    // Only compression method 0 (stored) has a 1:1 byte mapping.
                    if (FindProperty(fhType, "IsStored")?.GetValue(fileHeader) as bool? != true)
                    {
                        return Fail(logger, entryKey, "entry is compressed (not stored)");
                    }

                    // Reject encrypted parts (R4Salt / Rar5CryptoInfo non-null).
                    if (FindProperty(fhType, "R4Salt")?.GetValue(fileHeader) is not null
                        || FindProperty(fhType, "Rar5CryptoInfo")?.GetValue(fileHeader) is not null)
                    {
                        return Fail(logger, entryKey, "part is encrypted");
                    }

                    var dataStart = FindProperty(fhType, "DataStartPosition")?.GetValue(fileHeader) as long?;
                    var packed = FindProperty(fhType, "CompressedSize")?.GetValue(fileHeader) as long?;
                    if (dataStart is null || packed is null || packed < 0)
                    {
                        return Fail(logger, entryKey, "missing data offset/size in FileHeader");
                    }

                    if (filePart.Index < 0 || filePart.Index >= volumeFiles.Count)
                    {
                        return Fail(logger, entryKey, $"part index {filePart.Index} outside volume list ({volumeFiles.Count})");
                    }

                    segments.Add(new StoredRarSegment(volumeFiles[filePart.Index].FullName, dataStart.Value, packed.Value));
                    totalStored += packed.Value;
                }

                if (segments.Count == 0 || totalStored != entry.Size)
                {
                    return Fail(logger, entryKey, $"mapped {totalStored} of {entry.Size} bytes over {segments.Count} segment(s)");
                }

                return segments;
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "Failed to build stored-entry map for {Entry}; falling back to sequential stream", entryKey);
                return null;
            }
        }

        private static IReadOnlyList<StoredRarSegment>? Fail(ILogger logger, string entryKey, string reason)
        {
            logger.LogInformation("Direct stored access unavailable for {Entry}: {Reason}; using sequential stream", entryKey, reason);
            return null;
        }

        private static FieldInfo? FindField(Type type, string name)
        {
            for (Type? t = type; t is not null; t = t.BaseType)
            {
                var field = t.GetField(name, AnyInstance);
                if (field is not null)
                {
                    return field;
                }
            }

            return null;
        }

        private static PropertyInfo? FindProperty(Type type, string name)
        {
            for (Type? t = type; t is not null; t = t.BaseType)
            {
                var property = t.GetProperty(name, AnyInstance);
                if (property is not null)
                {
                    return property;
                }
            }

            return null;
        }
    }
}
