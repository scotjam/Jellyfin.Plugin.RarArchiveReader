using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Post-scan task that detects and mounts RAR archives after library scans.
    /// </summary>
    public class RarArchivePostScanTask : ILibraryPostScanTask
    {
        private readonly ILogger<RarArchivePostScanTask> _logger;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="RarArchivePostScanTask"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="libraryManager">The library manager, used to prune stale database items.</param>
        public RarArchivePostScanTask(ILogger<RarArchivePostScanTask> logger, ILibraryManager libraryManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
        }

        /// <inheritdoc />
        public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config?.AutoScanEnabled != true)
                {
                    _logger.LogInformation("RAR archive scanning is disabled in plugin configuration");
                    return;
                }

                _logger.LogInformation("Starting RAR archive post-scan task");

                var fileSystem = Plugin.GetFileSystem();

                // Get all library paths from Jellyfin configuration
                var libraryPaths = GetLibraryPaths();

                if (libraryPaths.Count == 0)
                {
                    _logger.LogWarning("No library paths found to scan for RAR archives");
                    return;
                }

                _logger.LogInformation("Scanning {Count} library paths for RAR archives", libraryPaths.Count);

                var rarFiles = new List<string>();
                var deniedPaths = new List<string>();
                int currentPath = 0;

                foreach (var path in libraryPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    _logger.LogDebug("Scanning path: {Path}", path);

                    try
                    {
                        var filesInPath = RarFileSystem.FindRarFiles(path, deniedPaths, cancellationToken);
                        rarFiles.AddRange(filesInPath);
                        _logger.LogDebug("Found {Count} RAR files in {Path}", filesInPath.Count, path);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error scanning path {Path} for RAR files", path);
                    }

                    currentPath++;
                    progress.Report((double)currentPath / libraryPaths.Count * 50); // First 50% is scanning
                }

                RarFileSystem.LogDeniedPaths(_logger, deniedPaths);

                _logger.LogInformation("Found {Count} RAR archives to process", rarFiles.Count);

                if (rarFiles.Count == 0)
                {
                    progress.Report(100);
                    return;
                }

                int processedCount = 0;
                int mountedCount = 0;

                // Media file names (e.g. "movie.mkv") found inside the RAR archives this run.
                // Used to safely identify stale DB rows that are leftovers of RAR content.
                var rarMediaFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var rarFile in rarFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        _logger.LogInformation("Processing RAR archive: {Archive}", rarFile);

                        // Check if this archive contains media files
                        var entries = fileSystem.GetArchiveEntries(rarFile);
                        var mediaEntries = entries.Where(e => IsMediaFile(e.Key, config)).ToList();

                        if (mediaEntries.Count == 0)
                        {
                            _logger.LogDebug("Archive {Archive} contains no media files, skipping", rarFile);
                            processedCount++;
                            continue;
                        }

                        foreach (var mediaEntry in mediaEntries)
                        {
                            var name = Path.GetFileName(mediaEntry.Key);
                            if (!string.IsNullOrEmpty(name))
                            {
                                rarMediaFileNames.Add(name);
                            }
                        }

                        _logger.LogInformation("Archive {Archive} contains {Count} media files", rarFile, mediaEntries.Count);

                        // Create .strm files for direct streaming without extraction
                        var strmCount = CreateStrmFiles(rarFile, entries, config);
                        if (strmCount > 0)
                        {
                            _logger.LogInformation("Created {Count} .strm files for {Archive}", strmCount, rarFile);
                            mountedCount += strmCount;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing RAR archive: {Archive}", rarFile);
                    }

                    processedCount++;
                    progress.Report(50 + ((double)processedCount / rarFiles.Count * 50)); // Last 50% is processing
                }

                _logger.LogInformation("RAR archive post-scan complete. Processed {Processed}/{Total} archives, mounted {Mounted}",
                    processedCount, rarFiles.Count, mountedCount);

                // Clean up orphaned .strm files (pointing to non-existent RAR archives)
                CleanupOrphanedStrmFiles(libraryPaths);

                // Clean up stale DB rows whose backing file no longer exists (e.g. left over
                // from an older plugin version that registered extracted/mounted media paths).
                CleanupStaleDbItems(libraryPaths, rarMediaFileNames);

                progress.Report(100);
                await Task.CompletedTask;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("RAR archive post-scan task was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RAR archive post-scan task");
                throw;
            }
        }

        private List<string> GetLibraryPaths()
        {
            var paths = new HashSet<string>();

            // Read from Jellyfin's library configuration files
            var configBasePaths = new[]
            {
                "/config/data/root/default",  // linuxserver container
                "/var/lib/jellyfin/root/default",  // native Linux install
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "jellyfin", "root", "default"),  // Windows (portable)
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Jellyfin", "Server", "root", "default"),  // Windows (standard install)
            };

            foreach (var configBasePath in configBasePaths)
            {
                if (!Directory.Exists(configBasePath))
                {
                    continue;
                }

                _logger.LogDebug("Searching for library config in: {Path}", configBasePath);

                try
                {
                    // Find all options.xml files in library subdirectories
                    var optionsFiles = Directory.EnumerateFiles(configBasePath, "options.xml", SearchOption.AllDirectories);

                    foreach (var optionsFile in optionsFiles)
                    {
                        try
                        {
                            var xml = XDocument.Load(optionsFile);

                            // Look for Path elements inside PathInfos/MediaPathInfo
                            var pathElements = xml.Descendants("Path");

                            foreach (var pathElement in pathElements)
                            {
                                var path = pathElement.Value?.Trim();
                                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                {
                                    paths.Add(path);
                                    _logger.LogDebug("Found library path: {Path}", path);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error reading options file: {File}", optionsFile);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error scanning config path: {Path}", configBasePath);
                }
            }

            if (paths.Count == 0)
            {
                _logger.LogWarning("No library paths found in any Jellyfin configuration location");
            }

            return paths.ToList();
        }

        private bool IsMediaFile(string filename, Configuration.PluginConfiguration config)
        {
            var extension = Path.GetExtension(filename).ToLowerInvariant();

            var allExtensions = new List<string>();

            if (!string.IsNullOrEmpty(config.SupportedVideoExtensions))
            {
                allExtensions.AddRange(config.SupportedVideoExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries));
            }

            if (!string.IsNullOrEmpty(config.SupportedAudioExtensions))
            {
                allExtensions.AddRange(config.SupportedAudioExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries));
            }

            if (!string.IsNullOrEmpty(config.SupportedImageExtensions))
            {
                allExtensions.AddRange(config.SupportedImageExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries));
            }

            return allExtensions.Any(ext => ext.Trim().Equals(extension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Creates or updates .strm files for media entries in a RAR archive.
        /// These files allow Jellyfin to see the media and stream directly from the archive.
        /// Updates existing .strm files if the RAR archive path has changed.
        /// </summary>
        /// <param name="rarFile">Path to the RAR archive.</param>
        /// <param name="entries">List of entries in the archive.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>Number of .strm files created or updated.</returns>
        private int CreateStrmFiles(string rarFile, List<ArchiveEntryInfo> entries, Configuration.PluginConfiguration config)
        {
            int createdCount = 0;
            var archiveDir = Path.GetDirectoryName(rarFile);

            if (string.IsNullOrEmpty(archiveDir))
            {
                return 0;
            }

            foreach (var entry in entries)
            {
                if (!IsMediaFile(entry.Key, config))
                {
                    continue;
                }

                try
                {
                    // Get the media filename from the entry
                    var mediaFileName = Path.GetFileName(entry.Key);
                    var strmFileName = Path.ChangeExtension(mediaFileName, ".strm");

                    // Put .strm file in the same directory as the RAR file
                    // This ensures Jellyfin uses the correct folder name for the show
                    string strmPath = Path.Combine(archiveDir, strmFileName);

                    // Create the expected streaming URL
                    // Format: http://localhost:8096/RarStream/{encodedArchivePath}/{encodedEntryPath}
                    var encodedArchivePath = HttpUtility.UrlEncode(rarFile);
                    var encodedEntryPath = HttpUtility.UrlEncode(entry.Key);
                    var streamUrl = $"http://localhost:8096/RarStream/{encodedArchivePath}/{encodedEntryPath}";

                    // Check if .strm file already exists
                    if (File.Exists(strmPath))
                    {
                        // Read existing content and check if it needs updating
                        var existingContent = File.ReadAllText(strmPath).Trim();
                        if (existingContent == streamUrl)
                        {
                            _logger.LogDebug("STRM file already up to date: {Path}", strmPath);
                            createdCount++;
                            continue;
                        }

                        // URL has changed (RAR file moved), update the .strm file
                        _logger.LogInformation("Updating STRM file with new RAR path: {Path}", strmPath);
                        File.WriteAllText(strmPath, streamUrl);
                        _logger.LogDebug("Updated STRM file: {Path} -> {Url}", strmPath, streamUrl);
                        createdCount++;
                        continue;
                    }

                    // Write the new .strm file
                    File.WriteAllText(strmPath, streamUrl);
                    _logger.LogDebug("Created STRM file: {Path} -> {Url}", strmPath, streamUrl);
                    createdCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create/update .strm file for entry: {Entry}", entry.Key);
                }
            }

            return createdCount;
        }

        /// <summary>
        /// Cleans up orphaned .strm files that point to non-existent RAR archives.
        /// </summary>
        /// <param name="libraryPaths">List of library paths to scan.</param>
        private void CleanupOrphanedStrmFiles(List<string> libraryPaths)
        {
            int removedCount = 0;

            foreach (var libraryPath in libraryPaths)
            {
                try
                {
                    var strmFiles = Directory.EnumerateFiles(libraryPath, "*.strm", SearchOption.AllDirectories);

                    foreach (var strmFile in strmFiles)
                    {
                        try
                        {
                            var content = File.ReadAllText(strmFile).Trim();

                            // Check if this is a RarStream .strm file
                            if (!content.Contains("/RarStream/"))
                            {
                                continue;
                            }

                            // Extract the RAR archive path from the URL
                            // Format: http://localhost:8096/RarStream/{encodedArchivePath}/{encodedEntryPath}
                            var match = System.Text.RegularExpressions.Regex.Match(
                                content,
                                @"/RarStream/([^/]+)/");

                            if (!match.Success)
                            {
                                continue;
                            }

                            var encodedPath = match.Groups[1].Value;
                            var rarPath = HttpUtility.UrlDecode(encodedPath);

                            // Check if the RAR archive still exists
                            if (!File.Exists(rarPath))
                            {
                                _logger.LogInformation("Removing orphaned STRM file (RAR not found): {StrmFile}", strmFile);
                                File.Delete(strmFile);
                                removedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error checking STRM file: {File}", strmFile);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error scanning for orphaned STRM files in: {Path}", libraryPath);
                }
            }

            if (removedCount > 0)
            {
                _logger.LogInformation("Removed {Count} orphaned STRM files", removedCount);
            }
        }

        /// <summary>
        /// Removes stale Jellyfin database items whose backing file no longer exists on disk.
        /// </summary>
        /// <remarks>
        /// Jellyfin only prunes a library item when the folder that should contain it is
        /// actually scanned. Items left behind by an older version of this plugin (which
        /// registered extracted/mounted media paths such as
        /// <c>/library/show/show.mkv</c> that were later replaced by the RAR set + a
        /// <c>.strm</c>) live in a phantom folder that is never visited again, so they
        /// linger forever — visible in the UI but failing playback with "Could not find file".
        /// <para>
        /// To stay safe this only deletes an item when ALL of the following hold:
        /// its file (and folder) is genuinely missing; its file name matches a media file
        /// found inside a RAR archive this run (so it is provably RAR-related, not an
        /// unrelated library file); and it sits under a configured library root that is
        /// currently online. The last two guards prevent mass-deletion during a temporary
        /// mount/disk outage.
        /// </para>
        /// </remarks>
        /// <param name="libraryPaths">Configured library roots that were scanned for RAR archives.</param>
        /// <param name="rarMediaFileNames">Media file names discovered inside RAR archives this run.</param>
        private void CleanupStaleDbItems(List<string> libraryPaths, HashSet<string> rarMediaFileNames)
        {
            // Nothing to match against -> do nothing (also avoids acting on an empty/failed scan).
            if (rarMediaFileNames.Count == 0)
            {
                return;
            }

            // Only consider library roots that are currently online, so we never prune
            // items just because a mount happens to be unavailable during this scan.
            var onlineRoots = libraryPaths
                .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
                .Select(p => p.TrimEnd('/', '\\') + Path.DirectorySeparatorChar)
                .ToList();

            if (onlineRoots.Count == 0)
            {
                _logger.LogDebug("Skipping stale DB cleanup: no configured library roots are currently online");
                return;
            }

            IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> items;
            try
            {
                items = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    IncludeItemTypes = new[]
                    {
                        Jellyfin.Data.Enums.BaseItemKind.Movie,
                        Jellyfin.Data.Enums.BaseItemKind.Episode,
                        Jellyfin.Data.Enums.BaseItemKind.Video,
                    },
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stale DB cleanup: failed to query library items");
                return;
            }

            int removedCount = 0;

            foreach (var item in items)
            {
                try
                {
                    var path = item.Path;
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    // The backing media is still present -> not stale, leave it alone.
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        continue;
                    }

                    // Only touch items whose file name matches media we found inside a RAR
                    // this run. This is what makes the deletion provably RAR-related.
                    var fileName = Path.GetFileName(path);
                    if (string.IsNullOrEmpty(fileName) || !rarMediaFileNames.Contains(fileName))
                    {
                        continue;
                    }

                    // The phantom path must sit under an online library root.
                    var normalized = path.Replace('\\', '/');
                    var underOnlineRoot = onlineRoots.Any(root =>
                        normalized.StartsWith(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                    if (!underOnlineRoot)
                    {
                        continue;
                    }

                    _logger.LogInformation(
                        "Removing stale DB item (file missing, matches RAR content): \"{Name}\" -> {Path}",
                        item.Name,
                        path);

                    _libraryManager.DeleteItem(
                        item,
                        new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = false },
                        notifyParentItem: true);
                    removedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Stale DB cleanup: failed to remove item {Path}", item.Path);
                }
            }

            if (removedCount > 0)
            {
                _logger.LogInformation("Removed {Count} stale DB item(s) with missing files", removedCount);
            }
        }
    }
}
