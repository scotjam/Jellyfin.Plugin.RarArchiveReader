using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaBrowser.Controller.Library;
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
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="RarArchiveStartupTask"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="libraryMonitor">Library monitor, used to refresh folders that received new STRM files.</param>
        /// <param name="libraryManager">Library manager, used as a fallback to queue a full scan.</param>
        public RarArchiveStartupTask(ILogger<RarArchiveStartupTask> logger, ILibraryMonitor libraryMonitor, ILibraryManager libraryManager)
        {
            _logger = logger;
            _libraryMonitor = libraryMonitor;
            _libraryManager = libraryManager;
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
                var changedStrmFiles = new List<string>();

                foreach (var rarFile in rarFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var entries = fileSystem.GetArchiveEntries(rarFile);
                        var hasMedia = entries.Any(e => StrmFileHelper.IsMediaFile(e.Key, config));

                        if (!hasMedia)
                        {
                            _logger.LogDebug("Archive contains no media files, skipping: {Archive}", rarFile);
                            processedCount++;
                            continue;
                        }

                        var result = StrmFileHelper.CreateStrmFiles(_logger, rarFile, entries, config);
                        strmCount += result.Total;
                        changedStrmFiles.AddRange(result.Changed);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing RAR archive: {Archive}", rarFile);
                    }

                    processedCount++;
                    progress.Report(30 + ((double)processedCount / rarFiles.Count * 60));
                }

                _logger.LogInformation("Processing complete: {StrmCount} STRM files ({Changed} new/updated) from {Total} RAR archives",
                    strmCount, changedStrmFiles.Count, rarFiles.Count);

                progress.Report(90);

                cancellationToken.ThrowIfCancellationRequested();

                // Step 4: Get Jellyfin to pick up the new/updated STRM files.
                if (changedStrmFiles.Count > 0)
                {
                    NotifyLibrary(changedStrmFiles);
                }

                progress.Report(100);
                _logger.LogInformation("RAR archive processing task complete");
                await Task.CompletedTask;
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
        /// Asks Jellyfin (in-process) to refresh the folders that received new or updated STRM files.
        /// Uses the same mechanism as real-time monitoring, so only the affected library folders are
        /// re-validated. Falls back to queueing a full library scan if that fails.
        /// </summary>
        private void NotifyLibrary(List<string> changedStrmFiles)
        {
            try
            {
                foreach (var strmPath in changedStrmFiles)
                {
                    _libraryMonitor.ReportFileSystemChanged(strmPath);
                }

                _logger.LogInformation("Requested library refresh for {Count} new/updated STRM file(s)", changedStrmFiles.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not request targeted library refresh; queueing a full library scan instead");
                try
                {
                    _libraryManager.QueueLibraryScan();
                }
                catch (Exception ex2)
                {
                    _logger.LogWarning(ex2, "Could not queue library scan");
                }
            }
        }
    }
}
