using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.RarArchiveReader.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// The main plugin class for RAR Archive Reader.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private static readonly object _lock = new object();
        private static RarFileSystem? _fileSystem;
        private static ILoggerFactory? _loggerFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
        /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <inheritdoc />
        public override string Name => "RAR Archive Reader";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("a8f38b91-6c7d-4e9a-9f2b-1234567890ab");

        /// <summary>
        /// Gets the current plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <summary>
        /// Sets the logger factory for the plugin.
        /// </summary>
        /// <param name="loggerFactory">The logger factory.</param>
        public static void SetLoggerFactory(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
        }

        /// <summary>
        /// Gets the RAR file system instance.
        /// </summary>
        public static RarFileSystem GetFileSystem()
        {
            if (_fileSystem == null)
            {
                lock (_lock)
                {
                    if (_fileSystem == null)
                    {
                        var logger = _loggerFactory?.CreateLogger<RarFileSystem>()
                            ?? new NullLogger<RarFileSystem>();
                        _fileSystem = new RarFileSystem(logger);
                    }
                }
            }
            return _fileSystem;
        }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = this.Name,
                    EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
                }
            };
        }
    }

    /// <summary>
    /// Null logger implementation for fallback.
    /// </summary>
    /// <typeparam name="T">The category type.</typeparam>
    internal class NullLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) => new NullScope();
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }

        private class NullScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
