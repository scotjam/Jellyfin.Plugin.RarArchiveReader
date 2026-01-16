using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>
        /// Initializes a new instance of the <see cref="RarArchivePostScanTask"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public RarArchivePostScanTask(ILogger<RarArchivePostScanTask> logger)
        {
            _logger = logger;
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
                var mountManager = Plugin.GetMountManager();

                // Get all library paths from Jellyfin configuration
                var libraryPaths = GetLibraryPaths();

                if (libraryPaths.Count == 0)
                {
                    _logger.LogWarning("No library paths found to scan for RAR archives");
                    return;
                }

                _logger.LogInformation("Scanning {Count} library paths for RAR archives", libraryPaths.Count);

                var rarFiles = new List<string>();
                int currentPath = 0;

                foreach (var path in libraryPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    _logger.LogDebug("Scanning path: {Path}", path);

                    try
                    {
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

                    currentPath++;
                    progress.Report((double)currentPath / libraryPaths.Count * 50); // First 50% is scanning
                }

                _logger.LogInformation("Found {Count} RAR archives to process", rarFiles.Count);

                if (rarFiles.Count == 0)
                {
                    progress.Report(100);
                    return;
                }

                int processedCount = 0;
                int mountedCount = 0;

                foreach (var rarFile in rarFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        _logger.LogInformation("Processing RAR archive: {Archive}", rarFile);

                        // Check if this archive contains media files
                        var entries = fileSystem.GetArchiveEntries(rarFile);
                        var hasMedia = entries.Any(e => IsMediaFile(e.Key, config));

                        if (!hasMedia)
                        {
                            _logger.LogDebug("Archive {Archive} contains no media files, skipping", rarFile);
                            processedCount++;
                            continue;
                        }

                        _logger.LogInformation("Archive {Archive} contains {Count} media files", rarFile, entries.Count(e => IsMediaFile(e.Key, config)));

                        // Try to mount with rar2fs if configured
                        if (config.PreferRar2fs && mountManager.IsRar2fsAvailable)
                        {
                            var mountPoint = mountManager.MountArchive(rarFile);
                            if (mountPoint != null)
                            {
                                _logger.LogInformation("Successfully mounted {Archive} at {MountPoint}", rarFile, mountPoint);
                                mountedCount++;
                            }
                            else
                            {
                                _logger.LogWarning("Failed to mount {Archive} with rar2fs", rarFile);
                            }
                        }
                        else
                        {
                            _logger.LogDebug("rar2fs not available or not preferred, archive will use fallback mode: {Archive}", rarFile);
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
            var paths = new List<string>();

            // Common Jellyfin mount points - adjust based on your setup
            var potentialPaths = new[]
            {
                "/tv",
                "/kidstv",
                "/movies",
                "/kidsmovies",
                "/media"
            };

            foreach (var path in potentialPaths)
            {
                if (Directory.Exists(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
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
    }
}
