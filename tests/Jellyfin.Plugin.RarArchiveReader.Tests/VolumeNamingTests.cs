using Xunit;

namespace Jellyfin.Plugin.RarArchiveReader.Tests
{
    /// <summary>
    /// Only the first volume of a set should be processed. Treating later volumes as
    /// archives in their own right produced duplicate .strm files and wasted scans.
    /// </summary>
    public class VolumeNamingTests
    {
        [Theory]
        // First volumes.
        [InlineData("movie.rar", true)]
        [InlineData("movie.part1.rar", true)]
        [InlineData("movie.part01.rar", true)]
        [InlineData("movie.part001.rar", true)]
        [InlineData("MOVIE.PART1.RAR", true)]
        [InlineData("movie.cbr", true)]
        // Continuation volumes.
        [InlineData("movie.part2.rar", false)]
        [InlineData("movie.part02.rar", false)]
        [InlineData("movie.part10.rar", false)]
        [InlineData("movie.part003.rar", false)]
        // Not archives the resolver should open at all.
        [InlineData("movie.r00", false)]
        [InlineData("movie.r15", false)]
        [InlineData("movie.mkv", false)]
        [InlineData("movie.strm", false)]
        [InlineData("movie.nfo", false)]
        public void IsFirstVolume_ClassifiesVolumeNames(string fileName, bool expected)
        {
            Assert.Equal(expected, StrmFileHelper.IsFirstVolume(fileName));
        }

        [Theory]
        [InlineData("archive.rar", true)]
        [InlineData("archive.RAR", true)]
        [InlineData("archive.cbr", true)]
        [InlineData("archive.r00", false)]
        [InlineData("archive.mkv", false)]
        [InlineData("", false)]
        public void IsRarArchive_MatchesRarExtensions(string path, bool expected)
        {
            Assert.Equal(expected, RarFileSystem.IsRarArchive(path));
        }

        [Theory]
        [InlineData("/media/x.rar/inner.mkv", "/media/x.rar", "inner.mkv")]
        [InlineData("/media/x.rar", "/media/x.rar", "")]
        [InlineData("/media/a.cbr/b/c.mkv", "/media/a.cbr", "b/c.mkv")]
        public void ParseVirtualPath_SplitsArchiveAndEntry(string input, string archive, string entry)
        {
            var parsed = RarFileSystem.ParseVirtualPath(input);

            Assert.NotNull(parsed);
            Assert.Equal(archive, parsed!.Value.ArchivePath);
            Assert.Equal(entry, parsed.Value.EntryPath);
        }

        [Fact]
        public void ParseVirtualPath_ReturnsNullWhenNoArchiveInPath()
        {
            Assert.Null(RarFileSystem.ParseVirtualPath("/media/movie.mkv"));
            Assert.Null(RarFileSystem.ParseVirtualPath(string.Empty));
        }
    }
}
