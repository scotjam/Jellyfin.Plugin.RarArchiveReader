using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.RarArchiveReader.Tests
{
    /// <summary>
    /// Some archivers write zero-padded volume numbers (<c>.part001.rar</c>).
    /// RAR does not record the file name, so renaming a committed fixture is a faithful stand-in.
    /// </summary>
    public class PaddedVolumeTests
    {
        [Fact]
        public void ThreeDigitPartNames_ReadAcrossAllVolumes()
        {
            var src = Path.Combine(RarFormatFixtures.ArchiveRoot, "rar5-store-multi");
            var dir = Directory.CreateTempSubdirectory("rar-padded-").FullName;
            try
            {
                foreach (var f in Directory.GetFiles(src))
                {
                    var name = Regex.Replace(Path.GetFileName(f), @"\.part(\d+)\.rar$", m => $".part{int.Parse(m.Groups[1].Value):D3}.rar");
                    File.Copy(f, Path.Combine(dir, name));
                }

                var first = Path.Combine(dir, "rar5-store-multi.part001.rar");
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
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
