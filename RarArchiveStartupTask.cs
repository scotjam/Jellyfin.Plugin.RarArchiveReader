using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml.Linq;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Scheduled task that creates STRM files for media inside RAR archives.
    /// Can be run manually or on a schedule (default: every 6 hours).
    /// </summary>
    public class RarArchiveStartupTask : IScheduledTask
    {
        private readonly ILogger<RarArchiveStartupTask> _logger;
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>
        /// Initializes a new instance of the <see cref="RarArchiveStartupTask"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public RarArchiveStartupTask(ILogger<RarArchiveStartupTask> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Process RAR Archives";

        /// <inheritdoc />
        public string Key => "RarArchiveMountTask";

        /// <inheritdoc />
        public string Description => "Creates STRM files for media inside RAR archives and triggers library scan";

        /// <inheritdoc />
        public string Category => "Library";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config?.AutoScanEnabled != true)
                {
                    _logger.LogInformation("RAR archive auto-scanning is disabled");
                    return;
                }

                _logger.LogInformation("Starting RAR archive processing task");
                progress.Report(0);

                var fileSystem = Plugin.GetFileSystem();

                progress.Report(10);
                cancellationToken.ThrowIfCancellationRequested();

                // Step 1: Discover library paths from Jellyfin config
                _logger.LogInformation("Discovering library paths from Jellyfin configuration...");
                var libraryPaths = GetLibraryPathsFromConfig();

                if (libraryPaths.Count == 0)
                {
                    _logger.LogWarning("No library paths found in Jellyfin configuration");
                    progress.Report(100);
                    return;
                }

                _logger.LogInformation("Found {Count} library paths: {Paths}",
                    libraryPaths.Count, string.Join(", ", libraryPaths));
                progress.Report(20);

                cancellationToken.ThrowIfCancellationRequested();

                // Step 2: Find all RAR files
                var rarFiles = new List<string>();
                var deniedPaths = new List<string>();
                foreach (var path in libraryPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!Directory.Exists(path))
                    {
                        _logger.LogDebug("Library path does not exist: {Path}", path);
                        continue;
                    }

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
                }

                RarFileSystem.LogDeniedPaths(_logger, deniedPaths);
                progress.Report(30);

                _logger.LogInformation("Found {Count} RAR archives", rarFiles.Count);

                if (rarFiles.Count == 0)
                {
                    _logger.LogInformation("No RAR archives found in library paths");
                    progress.Report(100);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Step 3: Create STRM files for each RAR archive (30-90% of progress)
                int processedCount = 0;
                int strmCount = 0;

                foreach (var rarFile in rarFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var entries = fileSystem.GetArchiveEntries(rarFile);
                        var hasMedia = entries.Any(e => IsMediaFile(e.Key, config));

                        if (!hasMedia)
                        {
                            _logger.LogDebug("Archive contains no media files, skipping: {Archive}", rarFile);
                            processedCount++;
                            continue;
                        }

                        var created = CreateStrmFiles(rarFile, entries, config);
                        if (created > 0)
                        {
                            _logger.LogInformation("Created {Count} STRM files for {Archive}", created, rarFile);
                            strmCount += created;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing RAR archive: {Archive}", rarFile);
                    }

                    processedCount++;
                    progress.Report(30 + ((double)processedCount / rarFiles.Count * 60));
                }

                _logger.LogInformation("Processing complete: created {StrmCount} STRM files from {Total} RAR archives",
                    strmCount, rarFiles.Count);

                progress.Report(90);

                cancellationToken.ThrowIfCancellationRequested();

                // Step 4: Trigger library scan
                if (strmCount > 0)
                {
                    _logger.LogInformation("Triggering library scan to detect newly created STRM files...");
                    await TriggerLibraryScanAsync(cancellationToken);
                }

                progress.Report(100);
                _logger.LogInformation("RAR archive processing task complete");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("RAR archive processing task was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RAR archive processing task");
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.StartupTrigger
                },
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(6).Ticks
                }
            };
        }

        /// <summary>
        /// Gets library paths from Jellyfin's XML configuration files.
        /// </summary>
        private List<string> GetLibraryPathsFromConfig()
        {
            var paths = new HashSet<string>();

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

                try
                {
                    var optionsFiles = Directory.EnumerateFiles(configBasePath, "options.xml", SearchOption.AllDirectories);

                    foreach (var optionsFile in optionsFiles)
                    {
                        try
                        {
                            var xml = XDocument.Load(optionsFile);
                            var pathElements = xml.Descendants("Path");

                            foreach (var pathElement in pathElements)
                            {
                                var path = pathElement.Value?.Trim();
                                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                {
                                    paths.Add(path);
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

        /// <summary>
        /// Triggers a Jellyfin library scan via the local HTTP API.
        /// </summary>
        private async Task TriggerLibraryScanAsync(CancellationToken cancellationToken)
        {
            try
            {
                var ports = new[] { 8096, 8920 };

                foreach (var port in ports)
                {
                    try
                    {
                        var url = $"http://localhost:{port}/Library/Refresh";
                        var response = await _httpClient.PostAsync(url, null, cancellationToken);

                        if (response.IsSuccessStatusCode)
                        {
                            _logger.LogInformation("Library scan triggered successfully on port {Port}", port);
                            return;
                        }
                    }
                    catch (HttpRequestException)
                    {
                        // Try next port
                    }
                }

                _logger.LogWarning("Could not trigger library scan - Jellyfin API not accessible");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error triggering library scan");
            }
        }

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
                    var mediaFileName = Path.GetFileName(entry.Key);
                    var strmFileName = Path.ChangeExtension(mediaFileName, ".strm");
                    string strmPath = Path.Combine(archiveDir, strmFileName);

                    var encodedArchivePath = HttpUtility.UrlEncode(rarFile);
                    var encodedEntryPath = HttpUtility.UrlEncode(entry.Key);
                    var streamUrl = $"http://localhost:8096/RarStream/{encodedArchivePath}/{encodedEntryPath}";

                    if (File.Exists(strmPath))
                    {
                        var existingContent = File.ReadAllText(strmPath).Trim();
                        if (existingContent == streamUrl)
                        {
                            _logger.LogDebug("STRM file already up to date: {Path}", strmPath);
                            createdCount++;
                            continue;
                        }

                        _logger.LogInformation("Updating STRM file with new RAR path: {Path}", strmPath);
                        File.WriteAllText(strmPath, streamUrl);
                        createdCount++;
                        continue;
                    }

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

        private bool IsMediaFile(string filename, Configuration.PluginConfiguration config)
        {
            var extension = Path.GetExtension(filename).ToLowerInvariant();
            var allExtensions = GetMediaExtensions(config);
            return allExtensions.Any(ext => ext.Trim().Equals(extension, StringComparison.OrdinalIgnoreCase));
        }

        private List<string> GetMediaExtensions(Configuration.PluginConfiguration config)
        {
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

            return allExtensions.Select(e => e.Trim()).ToList();
        }
    }
}
