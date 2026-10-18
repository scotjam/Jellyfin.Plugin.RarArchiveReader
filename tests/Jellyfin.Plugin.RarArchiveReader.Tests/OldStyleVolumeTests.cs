using System.IO;
using Xunit;

namespace Jellyfin.Plugin.RarArchiveReader.Tests
{
    /// <summary>
    /// Old-style sets (<c>.rar</c>, <c>.r00</c>, <c>.r01</c>...) whose base name ends in <c>.partN</c>
    /// look like new-style names. Going by the name alone opened them as incomplete archives.
    /// RAR does not record the file name, so renaming a committed fixture is a faithful stand-in.
    /// </summary>
    public class OldStyleVolumeTests
    {
        [Theory]
        [InlineData("show.part01")]
        [InlineData("show.extras.part1")]
        [InlineData("show.extras.part2")]
        public void PartLikeBaseName_ReadAcrossAllVolumes(string baseName)
        {
            var src = Path.Combine(RarFormatFixtures.ArchiveRoot, "rar4-store-oldvol");
            var dir = Directory.CreateTempSubdirectory("rar-oldvol-").FullName;
            try
            {
                foreach (var f in Directory.GetFiles(src))
                {
                    File.Copy(f, Path.Combine(dir, baseName + Path.GetExtension(f)));
                }

                var first = Path.Combine(dir, baseName + ".rar");
                Assert.True(StrmFileHelper.IsFirstVolume(first));

                using var fs = RarFormatFixtures.CreateFileSystem();
                var reader = fs.GetArchiveReader(first);
                Assert.NotNull(reader);

                using var stream = reader!.GetEntryStream(RarFormatFixtures.PayloadEntry);
                Assert.NotNull(stream);
                using var buffer = new MemoryStream();
                stream!.CopyTo(buffer);

                var expected = RarFormatFixtures.SourceBytes(RarFormatFixtures.PayloadEntry);
                Assert.Equal(RarFormatFixtures.Sha256(expected), RarFormatFixtures.Sha256(buffer.ToArray()));

                // Playback goes through the stored-segment map, which reads the volume list back out of SharpCompress.
                var segments = reader.TryGetStoredSegments(RarFormatFixtures.PayloadEntry);
                Assert.NotNull(segments);
                using var direct = new StoredRarStream(segments!);
                using var directBuffer = new MemoryStream();
                direct.CopyTo(directBuffer);
                Assert.Equal(RarFormatFixtures.Sha256(expected), RarFormatFixtures.Sha256(directBuffer.ToArray()));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
