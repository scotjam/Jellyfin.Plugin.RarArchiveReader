using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Result of creating/updating STRM files for one archive.
    /// </summary>
    public sealed class StrmResult
    {
        /// <summary>Gets the STRM paths that were newly written this call.</summary>
        public List<string> Created { get; } = new();

        /// <summary>Gets the STRM paths whose content was rewritten (archive moved).</summary>
        public List<string> Updated { get; } = new();

        /// <summary>Gets the STRM paths that already existed and were already correct.</summary>
        public List<string> Unchanged { get; } = new();

        /// <summary>Gets the number of STRM files that now exist and are correct for this archive.</summary>
        public int Total => Created.Count + Updated.Count + Unchanged.Count;

        /// <summary>Gets the paths that changed on disk (created or updated).</summary>
        public IEnumerable<string> Changed => Created.Concat(Updated);
    }

    /// <summary>
    /// Shared STRM-file logic used by the scheduled task, the post-scan task and the item resolver.
    /// </summary>
    public static class StrmFileHelper
    {
        private static readonly Regex PartVolumeRegex = new(@"\.part(\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Returns true if the given path is the first volume of a RAR set (or a single-volume archive).
        /// <c>.rar</c> and <c>.part1.rar</c>/<c>.part01.rar</c> are first volumes; <c>.part2.rar</c> etc. are not.
        /// (<c>.r00</c>.. parts never match <see cref="RarFileSystem.IsRarArchive"/> in the first place.)
        /// </summary>
        /// <param name="path">Path to a file.</param>
        /// <returns>True for the first volume.</returns>
        public static bool IsFirstVolume(string path)
        {
            if (!RarFileSystem.IsRarArchive(path))
            {
                return false;
            }

            var match = PartVolumeRegex.Match(Path.GetFileName(path));
            if (!match.Success)
            {
                return true;
            }

            return int.TryParse(match.Groups[1].Value, out var n) && n == 1;
        }

        /// <summary>
        /// Checks whether a file name has one of the configured media extensions.
        /// </summary>
        /// <param name="filename">File name or path.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>True if the extension is configured as media.</returns>
        public static bool IsMediaFile(string filename, Configuration.PluginConfiguration config)
        {
            var extension = Path.GetExtension(filename);
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            return GetMediaExtensions(config).Any(ext => ext.Equals(extension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets all configured media extensions (video, audio, image), trimmed.
        /// </summary>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>List of extensions including the leading dot.</returns>
        public static List<string> GetMediaExtensions(Configuration.PluginConfiguration config)
        {
            var all = new List<string>();

            foreach (var list in new[] { config.SupportedVideoExtensions, config.SupportedAudioExtensions, config.SupportedImageExtensions })
            {
                if (!string.IsNullOrEmpty(list))
                {
                    all.AddRange(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }

            return all;
        }

        /// <summary>
        /// Builds the streaming URL that a STRM file should contain for an archive entry.
        /// </summary>
        /// <param name="rarFile">Path to the first RAR volume.</param>
        /// <param name="entryKey">Entry path inside the archive.</param>
        /// <returns>The stream URL.</returns>
        public static string GetStreamUrl(string rarFile, string entryKey)
        {
            var encodedArchivePath = HttpUtility.UrlEncode(rarFile);
            var encodedEntryPath = HttpUtility.UrlEncode(entryKey);
            return $"http://localhost:8096/RarStream/{encodedArchivePath}/{encodedEntryPath}";
        }

        /// <summary>
        /// Creates or updates .strm files (next to the archive) for every media entry in a RAR archive.
        /// Existing files whose content already matches are left untouched.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="rarFile">Path to the first RAR volume.</param>
        /// <param name="entries">Archive entries.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>What was created/updated/unchanged.</returns>
        public static StrmResult CreateStrmFiles(ILogger logger, string rarFile, IEnumerable<ArchiveEntryInfo> entries, Configuration.PluginConfiguration config)
        {
            var result = new StrmResult();
            var archiveDir = Path.GetDirectoryName(rarFile);

            if (string.IsNullOrEmpty(archiveDir))
            {
                return result;
            }

            foreach (var entry in entries)
            {
                if (!IsMediaFile(entry.Key, config))
                {
                    continue;
                }

                try
                {
                    var mediaFileName = Path.GetFileName(entry.Key);
                    var strmFileName = Path.ChangeExtension(mediaFileName, ".strm");

                    // Put the .strm next to the RAR so Jellyfin uses the release folder name.
                    var strmPath = Path.Combine(archiveDir, strmFileName);
                    var streamUrl = GetStreamUrl(rarFile, entry.Key);

                    if (File.Exists(strmPath))
                    {
                        var existingContent = File.ReadAllText(strmPath).Trim();
                        if (existingContent == streamUrl)
                        {
                            logger.LogDebug("STRM file already up to date: {Path}", strmPath);
                            result.Unchanged.Add(strmPath);
                            continue;
                        }

                        logger.LogInformation("Updating STRM file with new RAR path: {Path}", strmPath);
                        File.WriteAllText(strmPath, streamUrl);
                        result.Updated.Add(strmPath);
                        continue;
                    }

                    File.WriteAllText(strmPath, streamUrl);
                    logger.LogInformation("Created STRM file: {Path} -> {Url}", strmPath, streamUrl);
                    result.Created.Add(strmPath);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to create/update .strm file for entry: {Entry}", entry.Key);
                }
            }

            return result;
        }
    }
}
