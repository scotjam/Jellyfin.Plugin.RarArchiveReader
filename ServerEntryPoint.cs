using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Hosted service that runs when Jellyfin starts.
    /// This is more reliable than scheduled task startup triggers.
    /// </summary>
    public class RarMountHostedService : IHostedService
    {
        private readonly ILogger<RarMountHostedService> _logger;
        private CancellationTokenSource? _cts;

        /// <summary>
        /// Initializes a new instance of the <see cref="RarMountHostedService"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public RarMountHostedService(ILogger<RarMountHostedService> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config?.AutoScanEnabled != true)
                {
                    _logger.LogDebug("RAR archive auto-mounting is disabled");
                    return Task.CompletedTask;
                }

                if (!config.PreferRar2fs)
                {
                    _logger.LogDebug("rar2fs mounting is not preferred");
                    return Task.CompletedTask;
                }

                var mountManager = Plugin.GetMountManager();

                if (mountManager.IsRar2fsAvailable)
                {
                    _logger.LogInformation("RAR Archive Reader: rar2fs available, mounting archives...");
                    _cts = new CancellationTokenSource();
                    _ = Task.Run(() => MountArchivesAsync(_cts.Token));
                }
                else
                {
                    _logger.LogWarning("RAR Archive Reader: rar2fs not available. " +
                        "Run the install-rar2fs.sh script to enable RAR archive mounting.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during RAR Archive Reader startup");
            }

            return Task.CompletedTask;
        }

        private async Task MountArchivesAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Wait a bit for Jellyfin to fully initialize
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

                _logger.LogInformation("Starting RAR archive mount process");

                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    _logger.LogWarning("Plugin configuration is null, aborting mount");
                    return;
                }

                var mountManager = Plugin.GetMountManager();
                var fileSystem = Plugin.GetFileSystem();

                // Discover library paths
                var libraryPaths = GetLibraryPathsFromConfig();
                if (libraryPaths.Count == 0)
                {
                    _logger.LogWarning("No library paths found in Jellyfin configuration");
                    return;
                }

                _logger.LogInformation("Found {Count} library paths: {Paths}",
                    libraryPaths.Count, string.Join(", ", libraryPaths));

                // Find all RAR files
                var rarFiles = new List<string>();
                foreach (var path in libraryPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (!Directory.Exists(path))
                        {
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

                // Group by directory
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
                    return;
                }

                // Mount directories
                int mountedCount = 0;
                int skippedCount = 0;

                foreach (var directory in directoriesWithRar!)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        // Check if already mounted
                        var unrarPath = Path.Combine(directory!, "UNRAR");
                        if (Directory.Exists(unrarPath) && Directory.GetFileSystemEntries(unrarPath).Length > 0)
                        {
                            skippedCount++;
                            continue;
                        }

                        // Get a representative RAR file
                        var representativeRar = rarFiles.FirstOrDefault(f =>
                            Path.GetDirectoryName(f) == directory);

                        if (representativeRar == null)
                        {
                            continue;
                        }

                        // Check for media files
                        var entries = fileSystem.GetArchiveEntries(representativeRar);
                        var hasMedia = entries.Any(e => IsMediaFile(e.Key, config));

                        if (!hasMedia)
                        {
                            continue;
                        }

                        // Mount
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
                }

                _logger.LogInformation("Mounting complete: {Mounted} mounted, {Skipped} already mounted, {Total} total directories",
                    mountedCount, skippedCount, directoriesWithRar.Count);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Mount task was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in mount task");
            }
        }

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

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("RAR Archive Reader plugin stopping...");
            _cts?.Cancel();
            return Task.CompletedTask;
        }
    }
}
