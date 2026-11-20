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

        /// <summary>Gets the archive entry keys of the media files the STRM files point at.</summary>
        public List<string> EntryKeys { get; } = new();
    }

    /// <summary>
    /// Shared STRM-file logic used by the scheduled task, the post-scan task and the item resolver.
    /// </summary>
    public static class StrmFileHelper
    {
        private static readonly Regex PartVolumeRegex = new(@"\.part(\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Comment line written after the URL: "# rar-volumes {count}:{bytes}:{ticks} {mediaCount}".
        // Jellyfin skips '#' lines when reading a STRM, so it only matters to us.
        private const string StampPrefix = "# rar-volumes ";

        /// <summary>
        /// Brings the STRM files for one archive up to date. Opening a RAR reads the headers of every
        /// volume, which is the slow part of a scan, so the archive is only opened when its volume set
        /// differs from the stamp recorded in its STRM files (or they are missing).
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="rarFile">Path to the first RAR volume.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>What was created/updated/unchanged.</returns>
        public static StrmResult SyncArchive(ILogger logger, string rarFile, Configuration.PluginConfiguration config)
        {
            // Stamp before reading, so a set that changes mid-read gets read again next time.
            var stamp = FormatStamp(RarFileSystem.ComputeVolumeStamp(rarFile));

            var upToDate = FindStampedStrmFiles(rarFile, stamp, config);
            if (upToDate != null)
            {
                logger.LogDebug("RAR volumes unchanged since STRM files were written, skipping: {Archive}", rarFile);
                return upToDate;
            }

            var entries = Plugin.GetFileSystem().GetArchiveEntries(rarFile);
            return CreateStrmFiles(logger, rarFile, entries, config, stamp);
        }

        /// <summary>
        /// Formats a volume stamp for the STRM comment line.
        /// </summary>
        /// <param name="stamp">Stamp from <see cref="RarFileSystem.ComputeVolumeStamp"/>.</param>
        /// <returns>The stamp text, or null when the volumes could not be listed.</returns>
        public static string? FormatStamp((int Count, long TotalLength, long MaxTicks) stamp)
        {
            return stamp.Count == 0 ? null : $"{stamp.Count}:{stamp.TotalLength}:{stamp.MaxTicks}";
        }

        /// <summary>
        /// Returns the archive's existing STRM files when every one of them was written for the given
        /// volume stamp and the current stream URL, and none are missing. Returns null when the archive
        /// has to be opened.
        /// </summary>
        /// <param name="rarFile">Path to the first RAR volume.</param>
        /// <param name="stamp">Current volume stamp text.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>The up-to-date STRM files as an all-unchanged result, or null.</returns>
        public static StrmResult? FindStampedStrmFiles(string rarFile, string? stamp, Configuration.PluginConfiguration config)
        {
            var archiveDir = Path.GetDirectoryName(rarFile);
            if (stamp == null || string.IsNullOrEmpty(archiveDir))
            {
                return null;
            }

            var urlPrefix = GetStreamUrl(rarFile, string.Empty, config);
            var result = new StrmResult();
            var expected = -1;

            foreach (var strmPath in Directory.EnumerateFiles(archiveDir, "*.strm"))
            {
                var (url, stampLine) = ReadStrm(strmPath);
                if (url == null || !url.StartsWith(urlPrefix, StringComparison.Ordinal))
                {
                    continue; // Belongs to another archive, or the base URL changed.
                }

                var stampPrefix = StampPrefix + stamp + " ";
                if (stampLine == null
                    || !stampLine.StartsWith(stampPrefix, StringComparison.Ordinal)
                    || !int.TryParse(stampLine.AsSpan(stampPrefix.Length), out expected))
                {
                    return null;
                }

                result.Unchanged.Add(strmPath);
                result.EntryKeys.Add(HttpUtility.UrlDecode(url.Substring(urlPrefix.Length)));
            }

            return expected > 0 && result.Unchanged.Count == expected ? result : null;
        }

        private static (string? Url, string? StampLine) ReadStrm(string strmPath)
        {
            var lines = File.ReadAllLines(strmPath).Select(l => l.Trim()).ToList();
            return (
                lines.FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')),
                lines.FirstOrDefault(l => l.StartsWith(StampPrefix, StringComparison.Ordinal)));
        }

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

            // An old-style set (.rar, .r00...) can have a base name ending in ".partN"; its .rar is the first volume.
            return (int.TryParse(match.Groups[1].Value, out var n) && n == 1) || HasOldStyleVolumes(path);
        }

        /// <summary>
        /// Returns true if the archive continues in old-style volumes (<c>.r00</c>, <c>.r01</c>...),
        /// whatever its own name looks like.
        /// </summary>
        /// <param name="path">Path to a <c>.rar</c> volume.</param>
        /// <returns>True when a sibling <c>.r00</c> exists.</returns>
        public static bool HasOldStyleVolumes(string path)
        {
            // ponytail: exact-case ".r00" lookup; uppercase ".R00" sets on case-sensitive file systems still go by the name.
            return File.Exists(Path.ChangeExtension(path, ".r00"));
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
        /// The base URL comes from configuration: clients direct-play STRM files by fetching
        /// this URL themselves, so it must be an address they can reach - "localhost" is only
        /// reachable by server-side ffmpeg.
        /// </summary>
        /// <param name="rarFile">Path to the first RAR volume.</param>
        /// <param name="entryKey">Entry path inside the archive.</param>
        /// <param name="config">Plugin configuration (for the stream base URL).</param>
        /// <returns>The stream URL.</returns>
        public static string GetStreamUrl(string rarFile, string entryKey, Configuration.PluginConfiguration config)
        {
            var baseUrl = config.StreamBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "http://localhost:8096";
            }

            baseUrl = baseUrl.TrimEnd('/');

            var encodedArchivePath = HttpUtility.UrlEncode(rarFile);
            var encodedEntryPath = HttpUtility.UrlEncode(entryKey);
            return $"{baseUrl}/RarStream/{encodedArchivePath}/{encodedEntryPath}";
        }

        /// <summary>
        /// Creates or updates .strm files (next to the archive) for every media entry in a RAR archive.
        /// Existing files whose content already matches are left untouched.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="rarFile">Path to the first RAR volume.</param>
        /// <param name="entries">Archive entries.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="stamp">Volume stamp taken before the entries were read, or null to write no stamp.</param>
        /// <returns>What was created/updated/unchanged.</returns>
        public static StrmResult CreateStrmFiles(ILogger logger, string rarFile, IEnumerable<ArchiveEntryInfo> entries, Configuration.PluginConfiguration config, string? stamp)
        {
            var result = new StrmResult();
            var archiveDir = Path.GetDirectoryName(rarFile);

            if (string.IsNullOrEmpty(archiveDir))
            {
                return result;
            }

            var mediaEntries = entries.Where(e => IsMediaFile(e.Key, config)).ToList();
            var stampLine = stamp == null ? string.Empty : $"{StampPrefix}{stamp} {mediaEntries.Count}\n";

            foreach (var entry in mediaEntries)
            {
                try
                {
                    var mediaFileName = Path.GetFileName(entry.Key);
                    var strmFileName = Path.ChangeExtension(mediaFileName, ".strm");

                    // Put the .strm next to the RAR so Jellyfin uses the release folder name.
                    var strmPath = Path.Combine(archiveDir, strmFileName);
                    var streamUrl = GetStreamUrl(rarFile, entry.Key, config);
                    var content = streamUrl + "\n" + stampLine;
                    result.EntryKeys.Add(entry.Key);

                    if (File.Exists(strmPath))
                    {
                        if (ReadStrm(strmPath).Url == streamUrl)
                        {
                            // Same target: a stale stamp is ours alone, so refresh it without telling Jellyfin.
                            if (File.ReadAllText(strmPath) != content)
                            {
                                File.WriteAllText(strmPath, content);
                            }

                            logger.LogDebug("STRM file already up to date: {Path}", strmPath);
                            result.Unchanged.Add(strmPath);
                            continue;
                        }

                        logger.LogInformation("Updating STRM file with new RAR path: {Path}", strmPath);
                        File.WriteAllText(strmPath, content);
                        result.Updated.Add(strmPath);
                        continue;
                    }

                    File.WriteAllText(strmPath, content);
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
