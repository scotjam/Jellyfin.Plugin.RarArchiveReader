using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Finds the folders of every Jellyfin library.
    /// </summary>
    public static class LibraryPathResolver
    {
        /// <summary>
        /// Gets the existing library folders. Jellyfin's own library list comes first, so custom
        /// data directories and service installs work; the options.xml files in the usual data
        /// locations are only read when that list is empty.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="logger">The logger.</param>
        /// <returns>Distinct existing library folders.</returns>
        public static List<string> GetLibraryPaths(ILibraryManager libraryManager, ILogger logger)
        {
            var configured = new List<string>();

            try
            {
                foreach (var folder in libraryManager.GetVirtualFolders())
                {
                    if (folder.Locations != null)
                    {
                        configured.AddRange(folder.Locations);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read the library list from Jellyfin, falling back to options.xml");
            }

            if (configured.Count == 0)
            {
                configured.AddRange(ReadOptionsFiles(logger));
            }

            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in configured)
            {
                var path = raw?.Trim();
                if (string.IsNullOrEmpty(path) || !seen.Add(path))
                {
                    continue;
                }

                if (Directory.Exists(path))
                {
                    paths.Add(path);
                    logger.LogDebug("Found library path: {Path}", path);
                }
                else
                {
                    // Mapped network drives are not visible to a Jellyfin running as a Windows service.
                    logger.LogWarning("Library path {Path} is not accessible to the Jellyfin process; skipping it", path);
                }
            }

            if (paths.Count == 0)
            {
                logger.LogWarning("No accessible library paths found. Configured: {Paths}",
                    configured.Count == 0 ? "(none)" : string.Join(", ", configured));
            }

            return paths;
        }

        private static IEnumerable<string> ReadOptionsFiles(ILogger logger)
        {
            var configBasePaths = new[]
            {
                "/config/data/root/default",  // linuxserver container
                "/var/lib/jellyfin/root/default",  // native Linux install
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "jellyfin", "root", "default"),  // Windows (portable)
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Jellyfin", "Server", "root", "default"),  // Windows (standard install)
            };

            var paths = new List<string>();
            foreach (var configBasePath in configBasePaths.Where(Directory.Exists))
            {
                try
                {
                    foreach (var optionsFile in Directory.EnumerateFiles(configBasePath, "options.xml", SearchOption.AllDirectories))
                    {
                        try
                        {
                            paths.AddRange(XDocument.Load(optionsFile).Descendants("Path").Select(e => e.Value));
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Error reading options file: {File}", optionsFile);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Error scanning config path: {Path}", configBasePath);
                }
            }

            return paths;
        }
    }
}
