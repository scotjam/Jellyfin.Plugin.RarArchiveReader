using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RarArchiveReader.Configuration
{
    /// <summary>
    /// Plugin configuration.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets a value indicating whether to automatically scan for RAR archives.
        /// </summary>
        public bool AutoScanEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum file size (in MB) to read from archives (fallback mode only).
        /// </summary>
        public int MaxFileSizeMB { get; set; } = 500;

        /// <summary>
        /// Gets or sets the streaming buffer size in MB for RAR archive playback.
        /// Larger values use more memory but provide better seeking performance.
        /// </summary>
        public int StreamingBufferSizeMB { get; set; } = 264;

        /// <summary>
        /// Gets or sets a value indicating whether to cache archive metadata.
        /// </summary>
        public bool CacheMetadata { get; set; } = true;

        /// <summary>
        /// Gets or sets the base URL written into .strm files for the /RarStream endpoint.
        /// At playback time the plugin rewrites the URL's host to whatever address the client
        /// used to reach the server, so this rarely needs changing. It is used as written only
        /// by tools that read the .strm file without going through PlaybackInfo (e.g.
        /// server-side ffmpeg), for which the default localhost address is correct.
        /// </summary>
        public string StreamBaseUrl { get; set; } = "http://localhost:8096";

        /// <summary>
        /// Gets or sets the supported video extensions.
        /// </summary>
        public string SupportedVideoExtensions { get; set; } = ".mkv,.mp4,.avi,.mov,.wmv,.flv,.webm,.m4v";

        /// <summary>
        /// Gets or sets the supported audio extensions.
        /// </summary>
        public string SupportedAudioExtensions { get; set; } = ".mp3,.flac,.wav,.aac,.ogg,.m4a,.wma";

        /// <summary>
        /// Gets or sets the supported image extensions.
        /// </summary>
        public string SupportedImageExtensions { get; set; } = ".jpg,.jpeg,.png,.gif,.bmp,.webp";
    }
}
