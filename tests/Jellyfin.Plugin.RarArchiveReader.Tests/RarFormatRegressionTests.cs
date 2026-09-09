using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.RarArchiveReader.Tests
{
    /// <summary>
    /// Regression coverage across RAR format variants: RAR4 and RAR5, stored and
    /// compressed, single and multi-volume, and both volume naming schemes
    /// (<c>.partN.rar</c> and <c>.rar</c>/<c>.rNN</c>).
    /// </summary>
    public class RarFormatRegressionTests
    {
        [Theory]
        [MemberData(nameof(RarFormatFixtures.AllCases), MemberType = typeof(RarFormatFixtures))]
        public void Fixture_HasExpectedVolumesOnDisk(RarFixture fixture)
        {
            Assert.True(Directory.Exists(fixture.Directory), $"missing fixture dir: {fixture.Directory}");
            Assert.True(File.Exists(fixture.FirstVolumePath), $"missing first volume: {fixture.FirstVolumePath}");
            Assert.Equal(fixture.VolumeCount, Directory.GetFiles(fixture.Directory).Length);
        }

        [Theory]
        [MemberData(nameof(RarFormatFixtures.ReadableCases), MemberType = typeof(RarFormatFixtures))]
        public void Entries_AreListedWithCorrectSizes(RarFixture fixture)
        {
            using var fs = RarFormatFixtures.CreateFileSystem();
            var entries = fs.GetArchiveEntries(fixture.FirstVolumePath);

            Assert.Equal(2, entries.Count);

            var payload = entries.Single(e => e.Key.EndsWith(RarFormatFixtures.PayloadEntry, StringComparison.Ordinal));
            var noise = entries.Single(e => e.Key.EndsWith(RarFormatFixtures.NoiseEntry, StringComparison.Ordinal));

            Assert.Equal(RarFormatFixtures.SourceBytes(RarFormatFixtures.PayloadEntry).Length, payload.Size);
            Assert.Equal(RarFormatFixtures.SourceBytes(RarFormatFixtures.NoiseEntry).Length, noise.Size);
            Assert.False(payload.IsEncrypted);
        }

        /// <summary>
        /// The whole point of the plugin: bytes read back out must equal bytes put in,
        /// across every volume boundary.
        /// </summary>
        [Theory]
        [MemberData(nameof(RarFormatFixtures.ReadableCases), MemberType = typeof(RarFormatFixtures))]
        public void ExtractedContent_MatchesSourceExactly(RarFixture fixture)
        {
            using var fs = RarFormatFixtures.CreateFileSystem();
            var reader = fs.GetArchiveReader(fixture.FirstVolumePath);
            Assert.NotNull(reader);

            foreach (var entryName in new[] { RarFormatFixtures.PayloadEntry, RarFormatFixtures.NoiseEntry })
            {
                var expected = RarFormatFixtures.SourceBytes(entryName);

                using var stream = reader!.GetEntryStream(entryName);
                Assert.NotNull(stream);

                using var buffer = new MemoryStream();
                stream!.CopyTo(buffer);

                Assert.Equal(RarFormatFixtures.Sha256(expected), RarFormatFixtures.Sha256(buffer.ToArray()));
            }
        }

        /// <summary>
        /// Direct stored access must engage for stored entries and must refuse
        /// compressed ones - a silent switch to the sequential path is what made
        /// seeking unusable on large archives.
        /// </summary>
        [Theory]
        [MemberData(nameof(RarFormatFixtures.ReadableCases), MemberType = typeof(RarFormatFixtures))]
        public void StoredMap_IsBuiltOnlyForStoredEntries(RarFixture fixture)
        {
            using var fs = RarFormatFixtures.CreateFileSystem();
            var reader = fs.GetArchiveReader(fixture.FirstVolumePath);
            Assert.NotNull(reader);

            var segments = reader!.TryGetStoredSegments(RarFormatFixtures.PayloadEntry);

            if (fixture.Compressed)
            {
                Assert.Null(segments);
                return;
            }

            Assert.NotNull(segments);
            Assert.NotEmpty(segments!);

            // Segments must cover the entry exactly and reference real files.
            var expectedLength = RarFormatFixtures.SourceBytes(RarFormatFixtures.PayloadEntry).Length;
            Assert.Equal(expectedLength, segments!.Sum(s => s.Length));
            Assert.All(segments, s => Assert.True(File.Exists(s.VolumePath), $"segment volume missing: {s.VolumePath}"));
        }

        /// <summary>
        /// Random access through <see cref="StoredRarStream"/>, including reads that
        /// straddle volume boundaries and a tail read - the pattern players use to
        /// fetch a Matroska index before the first frame.
        /// </summary>
        [Theory]
        [MemberData(nameof(RarFormatFixtures.StoredCases), MemberType = typeof(RarFormatFixtures))]
        public void StoredStream_RandomAccess_MatchesSource(RarFixture fixture)
        {
            var expected = RarFormatFixtures.SourceBytes(RarFormatFixtures.PayloadEntry);

            using var fs = RarFormatFixtures.CreateFileSystem();
            var reader = fs.GetArchiveReader(fixture.FirstVolumePath);
            var segments = reader!.TryGetStoredSegments(RarFormatFixtures.PayloadEntry);
            Assert.NotNull(segments);

            using var stream = new StoredRarStream(segments!);
            Assert.Equal(expected.Length, stream.Length);

            // Offsets chosen to land before, on and after 100 KB volume seams,
            // plus the head and the very end of the entry.
            const int volumeSize = 100 * 1024;
            var offsets = new long[]
            {
                0,
                volumeSize - 64,
                volumeSize,
                volumeSize + 64,
                (2 * volumeSize) - 1,
                3 * volumeSize,
                expected.Length - 4096,
            };

            foreach (var offset in offsets)
            {
                var length = (int)Math.Min(8192, expected.Length - offset);
                stream.Position = offset;

                var actual = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = stream.Read(actual, read, length - read);
                    Assert.True(n > 0, $"unexpected EOF at offset {offset + read}");
                    read += n;
                }

                Assert.True(
                    expected.AsSpan((int)offset, length).SequenceEqual(actual),
                    $"content mismatch at offset {offset} in {fixture.Name}");
            }
        }

        [Theory]
        [MemberData(nameof(RarFormatFixtures.StoredCases), MemberType = typeof(RarFormatFixtures))]
        public void StoredStream_SequentialRead_MatchesSource(RarFixture fixture)
        {
            var expected = RarFormatFixtures.SourceBytes(RarFormatFixtures.PayloadEntry);

            using var fs = RarFormatFixtures.CreateFileSystem();
            var reader = fs.GetArchiveReader(fixture.FirstVolumePath);
            var segments = reader!.TryGetStoredSegments(RarFormatFixtures.PayloadEntry);

            using var stream = new StoredRarStream(segments!);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer, 4096);

            Assert.Equal(RarFormatFixtures.Sha256(expected), RarFormatFixtures.Sha256(buffer.ToArray()));
        }

        /// <summary>
        /// Compressed entries fall back to <see cref="RarBufferedStream"/>; it must still
        /// return correct bytes, including after seeking backwards.
        /// </summary>
        [Theory]
        [MemberData(nameof(RarFormatFixtures.CompressedCases), MemberType = typeof(RarFormatFixtures))]
        public void BufferedStream_ReadsCompressedEntries(RarFixture fixture)
        {
            if (!fixture.SupportedBySharpCompress)
            {
                return;
            }

            var expected = RarFormatFixtures.SourceBytes(RarFormatFixtures.PayloadEntry);

            using var stream = new RarBufferedStream(
                fixture.FirstVolumePath,
                RarFormatFixtures.PayloadEntry,
                expected.Length,
                bufferSizeMB: 1);

            Assert.Equal(expected.Length, stream.Length);

            var head = new byte[4096];
            stream.Position = 0;
            ReadExactly(stream, head);
            Assert.True(expected.AsSpan(0, head.Length).SequenceEqual(head), "head mismatch");

            var tail = new byte[4096];
            stream.Position = expected.Length - tail.Length;
            ReadExactly(stream, tail);
            Assert.True(expected.AsSpan(expected.Length - tail.Length).SequenceEqual(tail), "tail mismatch");

            // Seek backwards - forces the fallback to reopen the decompression stream.
            var again = new byte[4096];
            stream.Position = 0;
            ReadExactly(stream, again);
            Assert.True(expected.AsSpan(0, again.Length).SequenceEqual(again), "re-read after backward seek mismatch");
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                {
                    throw new EndOfStreamException($"got {read} of {buffer.Length} bytes");
                }

                read += n;
            }
        }
    }
}
