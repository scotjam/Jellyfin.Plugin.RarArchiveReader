using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Xunit;

namespace Jellyfin.Plugin.RarArchiveReader.Tests
{
    /// <summary>
    /// An old-style set without its .rar first volume cannot be opened. The scan must report it
    /// instead of skipping it silently.
    /// </summary>
    public sealed class MissingFirstVolumeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rar-missing-first-" + Guid.NewGuid().ToString("N"));

        public MissingFirstVolumeTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            Directory.Delete(_root, true);
        }

        [Fact]
        public void FindRarFiles_ReportsR00WithoutRar()
        {
            var orphanDir = Directory.CreateDirectory(Path.Combine(_root, "orphan")).FullName;
            File.WriteAllBytes(Path.Combine(orphanDir, "movie.r00"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(orphanDir, "movie.r01"), Array.Empty<byte>());

            var completeDir = Directory.CreateDirectory(Path.Combine(_root, "complete")).FullName;
            File.WriteAllBytes(Path.Combine(completeDir, "movie.rar"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(completeDir, "movie.r00"), Array.Empty<byte>());

            var missing = new List<string>();
            var found = RarFileSystem.FindRarFiles(_root, new List<string>(), CancellationToken.None, missing);

            Assert.Equal(new[] { Path.Combine(completeDir, "movie.rar") }, found);
            Assert.Equal(new[] { Path.Combine(orphanDir, "movie.r00") }, missing);
        }
    }
}
