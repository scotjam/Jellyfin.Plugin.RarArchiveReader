using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Item resolver that creates STRM files for RAR archives <em>while Jellyfin is scanning</em>.
    /// <para>
    /// Jellyfin calls every <see cref="IItemResolver"/> for each directory/file it encounters during
    /// any kind of scan: "Scan All Libraries", a single-library scan, and the real-time library
    /// monitor refresh that fires when a new folder appears. <see cref="ResolverPriority.Plugin"/>
    /// runs before the built-in Movie/Episode resolvers, so when this resolver writes a STRM file
    /// and appends it to <see cref="ItemResolveArgs.FileSystemChildren"/>, the built-in resolvers
    /// see it in the very same pass and create the playable item immediately.
    /// </para>
    /// <para>
    /// This resolver never produces items itself; it always returns <c>null</c>.
    /// </para>
    /// </summary>
    public class RarArchiveResolver : IItemResolver
    {
        private readonly ILogger<RarArchiveResolver> _logger;
        private readonly IFileSystem _fileSystem;
        private readonly ILibraryMonitor _libraryMonitor;

        /// <summary>
        /// Initializes a new instance of the <see cref="RarArchiveResolver"/> class.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="fileSystem">Jellyfin file system abstraction, used to build child metadata.</param>
        /// <param name="libraryMonitor">Library monitor, used to request a refresh when a STRM cannot be injected into the current pass.</param>
        public RarArchiveResolver(ILogger<RarArchiveResolver> logger, IFileSystem fileSystem, ILibraryMonitor libraryMonitor)
        {
            _logger = logger;
            _fileSystem = fileSystem;
            _libraryMonitor = libraryMonitor;
        }

        /// <summary>
        /// Gets the priority for this resolver. <see cref="ResolverPriority.Plugin"/> is the lowest enum value,
        /// i.e. it runs before all built-in resolvers.
        /// </summary>
        public ResolverPriority Priority => ResolverPriority.Plugin;

        /// <inheritdoc />
        public BaseItem? ResolvePath(ItemResolveArgs args)
        {
            if (args?.FileInfo == null || string.IsNullOrEmpty(args.Path))
            {
                return null;
            }

            var config = Plugin.Instance?.Configuration;
            if (config?.AutoScanEnabled != true)
            {
                return null;
            }

            try
            {
                if (args.IsDirectory)
                {
                    ProcessDirectory(args, config);
                }
                else if (StrmFileHelper.IsFirstVolume(args.Path))
                {
                    ProcessStandaloneArchive(args.Path, config);
                }
            }
            catch (Exception ex)
            {
                // Never break library scanning because of the plugin.
                _logger.LogWarning(ex, "RAR resolver failed for {Path}", args.Path);
            }

            return null;
        }

        /// <summary>
        /// A directory is being resolved (e.g. a movie release folder, or an episode folder inside a season).
        /// Create STRM files for every first-volume RAR it directly contains and make them visible to the
        /// resolvers that run after us in this pass.
        /// </summary>
        private void ProcessDirectory(ItemResolveArgs args, Configuration.PluginConfiguration config)
        {
            var children = args.FileSystemChildren;
            if (children == null || children.Length == 0)
            {
                return;
            }

            var rarFiles = children
                .Where(c => !c.IsDirectory && StrmFileHelper.IsFirstVolume(c.FullName))
                .Select(c => c.FullName)
                .ToList();

            if (rarFiles.Count == 0)
            {
                return;
            }

            var fileSystem = Plugin.GetFileSystem();
            var existing = new HashSet<string>(children.Select(c => c.FullName), StringComparer.OrdinalIgnoreCase);
            var toInject = new List<FileSystemMetadata>();

            foreach (var rarFile in rarFiles)
            {
                List<ArchiveEntryInfo> entries;
                try
                {
                    entries = fileSystem.GetArchiveEntries(rarFile);
                }
                catch (Exception ex)
                {
                    // Typically an archive that is still being written/moved. The next scan or the
                    // scheduled task will retry.
                    _logger.LogDebug(ex, "Could not read RAR archive during scan: {Archive}", rarFile);
                    continue;
                }

                var result = StrmFileHelper.CreateStrmFiles(_logger, rarFile, entries, config);

                foreach (var strmPath in result.Created.Concat(result.Updated).Concat(result.Unchanged))
                {
                    if (existing.Contains(strmPath))
                    {
                        continue;
                    }

                    var info = _fileSystem.GetFileInfo(strmPath);
                    if (info.Exists)
                    {
                        toInject.Add(info);
                        existing.Add(strmPath);
                    }
                }

                if (result.Created.Count > 0)
                {
                    _logger.LogInformation("Scan created {Count} STRM file(s) for {Archive}", result.Created.Count, rarFile);
                }
            }

            if (toInject.Count > 0)
            {
                // Make the new STRM(s) part of this directory listing so MovieResolver/EpisodeResolver
                // (which run after us) resolve them now rather than on a later scan.
                args.FileSystemChildren = children.Concat(toInject).ToArray();
                _logger.LogDebug("Injected {Count} STRM file(s) into scan of {Dir}", toInject.Count, args.Path);
            }
        }

        /// <summary>
        /// A RAR file itself is being resolved as a file (its parent directory listing has already been
        /// consumed, so we can't inject). Create the STRM and ask the library monitor to refresh the
        /// parent, which re-runs resolution with the STRM present.
        /// </summary>
        private void ProcessStandaloneArchive(string rarFile, Configuration.PluginConfiguration config)
        {
            List<ArchiveEntryInfo> entries;
            try
            {
                entries = Plugin.GetFileSystem().GetArchiveEntries(rarFile);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read RAR archive during scan: {Archive}", rarFile);
                return;
            }

            var result = StrmFileHelper.CreateStrmFiles(_logger, rarFile, entries, config);

            foreach (var strmPath in result.Changed)
            {
                _libraryMonitor.ReportFileSystemChanged(strmPath);
            }

            if (result.Created.Count > 0)
            {
                _logger.LogInformation("Scan created {Count} STRM file(s) for {Archive}; parent folder refresh requested", result.Created.Count, rarFile);
            }
        }
    }
}
