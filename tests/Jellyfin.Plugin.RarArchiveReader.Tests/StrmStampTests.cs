using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.RarArchiveReader.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.RarArchiveReader.Tests
{
    /// <summary>
    /// Scans skip opening an archive when its STRM files carry the current volume stamp.
    /// Opening reads every volume's headers, so a wrong "up to date" answer hides new media
    /// and a wrong "stale" answer makes every scan slow again.
    /// </summary>
    public sealed class StrmStampTests : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("rar-stamp-").FullName;
        private readonly PluginConfiguration _config = new();

        public void Dispose() => Directory.Delete(_dir, true);

        private string Rar => Path.Combine(_dir, "show.rar");

        private void Write(string stamp) => StrmFileHelper.CreateStrmFiles(
            NullLogger.Instance,
            Rar,
            new List<ArchiveEntryInfo> { new() { Key = "show.e01.mkv" }, new() { Key = "show.e02.mkv" }, new() { Key = "show.nfo" } },
            _config,
            stamp);

        [Fact]
        public void MatchingStamp_SkipsOpenAndReportsEntries()
        {
            Write("2:100:5");

            var result = StrmFileHelper.FindStampedStrmFiles(Rar, "2:100:5", _config);

            Assert.NotNull(result);
            Assert.Equal(2, result!.Unchanged.Count);
            Assert.Contains("show.e01.mkv", result.EntryKeys);
        }

        [Fact]
        public void StrmStartsWithUrl_SoJellyfinStillReadsIt()
        {
            Write("2:100:5");

            var lines = File.ReadAllLines(Path.Combine(_dir, "show.e01.strm"));

            Assert.StartsWith("http", lines[0]);
            Assert.StartsWith("#", lines[1]);
        }

        [Fact]
        public void ChangedVolumes_Reopen()
        {
            Write("2:100:5");

            Assert.Null(StrmFileHelper.FindStampedStrmFiles(Rar, "3:150:9", _config));
        }

        [Fact]
        public void DeletedStrm_Reopen()
        {
            Write("2:100:5");
            File.Delete(Path.Combine(_dir, "show.e02.strm"));

            Assert.Null(StrmFileHelper.FindStampedStrmFiles(Rar, "2:100:5", _config));
        }

        [Fact]
        public void OldUnstampedStrm_Reopen()
        {
            File.WriteAllText(Path.Combine(_dir, "show.e01.strm"), StrmFileHelper.GetStreamUrl(Rar, "show.e01.mkv", _config));

            Assert.Null(StrmFileHelper.FindStampedStrmFiles(Rar, "2:100:5", _config));
        }

        [Fact]
        public void ChangedBaseUrl_Reopen()
        {
            Write("2:100:5");
            _config.StreamBaseUrl = "http://media.lan:8096";

            Assert.Null(StrmFileHelper.FindStampedStrmFiles(Rar, "2:100:5", _config));
        }

        [Fact]
        public void UnlistableVolumes_Reopen()
        {
            Assert.Null(StrmFileHelper.FormatStamp((0, 0, 0)));
        }
    }
}
