using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Scheduled task that mounts RAR archives using rar2fs.
    /// Can be run manually or on a schedule (default: every 6 hours).
    /// Note: rar2fs must be pre-installed via the install-rar2fs.sh script.
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
        public string Name => "Mount RAR Archives";

        /// <inheritdoc />
        public string Key => "RarArchiveMountTask";

        /// <inheritdoc />
        public string Description => "Mounts RAR archives using rar2fs and triggers library scan";

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

                _logger.LogInformation("Starting RAR archive mounting task");
                progress.Report(0);

                var mountManager = Plugin.GetMountManager();
                var fileSystem = Plugin.GetFileSystem();

                // Check if rar2fs is available
                if (!config.PreferRar2fs || !mountManager.IsRar2fsAvailable)
                {
                    _logger.LogInformation("rar2fs is not available or not preferred, skipping mount task");
                    progress.Report(100);
                    return;
                }

                progress.Report(10);
                cancellationToken.ThrowIfCancellationRequested();

                // Step 2: Discover library paths from Jellyfin config (20% of progress)
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

                // Step 3: Find all RAR files (30% of progress)
                var rarFiles = new List<string>();
                foreach (var path in libraryPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (!Directory.Exists(path))
                        {
                            _logger.LogDebug("Library path does not exist: {Path}", path);
                            continue;
                        }

                        var filesInPath = Directory.EnumerateFiles(path, "*.rar", SearchOption.AllDirectories)
                            .Where(f => RarFileSystem.IsRarArchive(f))
                            .ToList();

                        rarFiles.AddRange(filesInPath);
                        _logger.LogDebug("Found {Count} RAR files in {Path}", filesInPath.Count, path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error scanning path {Path} for RAR files", path);
                    }
                }

                progress.Report(30);

                // Group RAR files by directory - rar2fs mounts directories, not individual files
                var directoriesWithRar = rarFiles
                    .Select(f => Path.GetDirectoryName(f))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .ToList();

                _logger.LogInformation("Found {Count} RAR archives in {DirCount} directories",
                    rarFiles.Count, directoriesWithRar.Count);

                if (directoriesWithRar.Count == 0)
                {
                    _logger.LogInformation("No RAR archives found in library paths");
                    progress.Report(100);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Step 4: Mount all directories (30-90% of progress)
                int processedCount = 0;
                int mountedCount = 0;
                int skippedCount = 0;

                foreach (var directory in directoriesWithRar!)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        // Check if already mounted (UNRAR folder exists and has content)
                        var unrarPath = Path.Combine(directory!, "UNRAR");
                        if (Directory.Exists(unrarPath) && Directory.GetFileSystemEntries(unrarPath).Length > 0)
                        {
                            _logger.LogDebug("Directory already mounted: {Directory}", directory);
                            skippedCount++;
                            processedCount++;
                            continue;
                        }

                        // Get a representative RAR file from this directory to check for media
                        var representativeRar = rarFiles.FirstOrDefault(f =>
                            Path.GetDirectoryName(f) == directory);

                        if (representativeRar == null)
                        {
                            processedCount++;
                            continue;
                        }

                        // Check if archive contains media files
                        var entries = fileSystem.GetArchiveEntries(representativeRar);
                        var hasMedia = entries.Any(e => IsMediaFile(e.Key, config));

                        if (!hasMedia)
                        {
                            _logger.LogDebug("Archive contains no media files, skipping: {Directory}", directory);
                            processedCount++;
                            continue;
                        }

                        // Try to mount
                        var mountPoint = mountManager.MountArchive(representativeRar);
                        if (mountPoint != null)
                        {
                            _logger.LogInformation("Mounted: {Directory} -> {MountPoint}", directory, mountPoint);
                            mountedCount++;
                        }
                        else
                        {
                            _logger.LogWarning("Failed to mount: {Directory}", directory);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error mounting RAR directory: {Directory}", directory);
                    }

                    processedCount++;
                    progress.Report(30 + ((double)processedCount / directoriesWithRar.Count * 60));
                }

                _logger.LogInformation("Mounting complete: {Mounted} mounted, {Skipped} already mounted, {Total} total directories",
                    mountedCount, skippedCount, directoriesWithRar.Count);

                progress.Report(90);

                cancellationToken.ThrowIfCancellationRequested();

                // Step 5: Trigger library scan (90-100% of progress)
                if (mountedCount > 0)
                {
                    _logger.LogInformation("Triggering library scan to detect newly mounted content...");
                    await TriggerLibraryScanAsync(cancellationToken);
                }

                progress.Report(100);
                _logger.LogInformation("RAR archive startup task complete");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("RAR archive startup task was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RAR archive startup task");
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Run on startup and periodically
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerStartup
                },
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerInterval,
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

            // Read from Jellyfin's library configuration files
            var configBasePaths = new[]
            {
                "/config/data/root/default",  // linuxserver container
                "/var/lib/jellyfin/root/default",  // native Linux install
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "jellyfin", "root", "default")  // Windows
            };

            foreach (var configBasePath in configBasePaths)
            {
                if (!Directory.Exists(configBasePath))
                {
                    continue;
                }

                try
                {
                    // Find all options.xml files in library subdirectories
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

            // Fallback to common mount points if no config found
            if (paths.Count == 0)
            {
                _logger.LogDebug("No library paths found in config, checking common mount points");
                var fallbackPaths = new[] { "/tv", "/movies", "/media", "/kidstv", "/kidsmovies" };
                foreach (var path in fallbackPaths)
                {
                    if (Directory.Exists(path))
                    {
                        paths.Add(path);
                    }
                }
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
                // Try common Jellyfin ports
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
