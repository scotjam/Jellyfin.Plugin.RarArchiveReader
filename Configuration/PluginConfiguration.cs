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
        /// Gets or sets a value indicating whether to prefer rar2fs over in-memory streaming.
        /// </summary>
        public bool PreferRar2fs { get; set; } = true;

        /// <summary>
        /// Gets or sets the base directory for rar2fs mount points.
        /// </summary>
        public string MountPointBase { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the maximum idle time (in minutes) before unmounting archives.
        /// </summary>
        public int MountIdleTimeoutMinutes { get; set; } = 30;

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
