using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net.Mime;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader.Api
{
    /// <summary>
    /// API controller for streaming media directly from RAR archives.
    /// </summary>
    [ApiController]
    [Route("RarStream")]
    public class RarStreamController : ControllerBase
    {
        private readonly ILogger<RarStreamController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RarStreamController"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        public RarStreamController(ILogger<RarStreamController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Streams a media file directly from a RAR archive.
        /// </summary>
        /// <param name="archivePath">URL-encoded path to the RAR archive.</param>
        /// <param name="entryPath">URL-encoded path to the file within the archive.</param>
        /// <returns>The media stream.</returns>
        [HttpGet("{archivePath}/{*entryPath}")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> GetStream(
            [FromRoute, Required] string archivePath,
            [FromRoute, Required] string entryPath)
        {
            try
            {
                // Decode the paths
                var decodedArchivePath = HttpUtility.UrlDecode(archivePath);
                var decodedEntryPath = HttpUtility.UrlDecode(entryPath);

                _logger.LogInformation("RAR stream request - Archive: {Archive}, Entry: {Entry}",
                    decodedArchivePath, decodedEntryPath);

                // Validate archive exists
                if (!System.IO.File.Exists(decodedArchivePath))
                {
                    _logger.LogWarning("Archive not found: {Archive}", decodedArchivePath);
                    return NotFound($"Archive not found: {decodedArchivePath}");
                }

                // Get the file system from the plugin
                var fileSystem = Plugin.GetFileSystem();
                if (fileSystem == null)
                {
                    _logger.LogError("RarFileSystem not available");
                    return StatusCode(500, "RAR file system not initialized");
                }

                // Get archive reader
                var reader = fileSystem.GetArchiveReader(decodedArchivePath);
                if (reader == null)
                {
                    _logger.LogWarning("Failed to open archive: {Archive}", decodedArchivePath);
                    return StatusCode(500, $"Failed to open archive: {decodedArchivePath}");
                }

                // Get entry info to check existence and get size
                var entries = reader.GetEntries();
                var entryInfo = entries.Find(e => e.Key == decodedEntryPath);
                if (entryInfo == null)
                {
                    _logger.LogWarning("Entry not found in archive: {Entry}", decodedEntryPath);
                    return NotFound($"Entry not found: {decodedEntryPath}");
                }

                // Get buffer size from configuration
                var config = Plugin.Instance?.Configuration;
                var bufferSizeMB = config?.StreamingBufferSizeMB ?? RarBufferedStream.DefaultBufferSizeMB;

                // Create a buffered stream for memory-efficient seeking
                var stream = new RarBufferedStream(
                    decodedArchivePath,
                    decodedEntryPath,
                    entryInfo.Size,
                    bufferSizeMB);

                // Determine content type based on file extension
                var contentType = GetContentType(decodedEntryPath);

                _logger.LogInformation("Streaming {Entry} ({ContentType}, {Size} bytes, {Buffer}MB buffer)",
                    decodedEntryPath, contentType, entryInfo.Size, bufferSizeMB);

                // Return the stream with range processing enabled (buffered stream supports seeking)
                return File(stream, contentType, enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming from RAR archive");
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets file information from a RAR archive entry.
        /// </summary>
        /// <param name="archivePath">URL-encoded path to the RAR archive.</param>
        /// <param name="entryPath">URL-encoded path to the file within the archive.</param>
        /// <returns>File information.</returns>
        [HttpHead("{archivePath}/{*entryPath}")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetInfo(
            [FromRoute, Required] string archivePath,
            [FromRoute, Required] string entryPath)
        {
            try
            {
                var decodedArchivePath = HttpUtility.UrlDecode(archivePath);
                var decodedEntryPath = HttpUtility.UrlDecode(entryPath);

                if (!System.IO.File.Exists(decodedArchivePath))
                {
                    return NotFound();
                }

                var fileSystem = Plugin.GetFileSystem();
                if (fileSystem == null)
                {
                    return StatusCode(500);
                }

                var reader = fileSystem.GetArchiveReader(decodedArchivePath);
                if (reader == null)
                {
                    return StatusCode(500);
                }

                var entries = reader.GetEntries();
                var entry = entries.Find(e => e.Key == decodedEntryPath);
                if (entry == null)
                {
                    return NotFound();
                }

                var contentType = GetContentType(decodedEntryPath);

                Response.Headers["Content-Type"] = contentType;
                Response.Headers["Content-Length"] = entry.Size.ToString();
                Response.Headers["Accept-Ranges"] = "bytes";

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting info from RAR archive");
                return StatusCode(500);
            }
        }

        private static string GetContentType(string fileName)
        {
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            return extension switch
            {
                ".mkv" => "video/x-matroska",
                ".mp4" => "video/mp4",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".wmv" => "video/x-ms-wmv",
                ".flv" => "video/x-flv",
                ".webm" => "video/webm",
                ".m4v" => "video/x-m4v",
                ".ts" => "video/mp2t",
                ".m2ts" => "video/mp2t",
                ".mp3" => "audio/mpeg",
                ".flac" => "audio/flac",
                ".wav" => "audio/wav",
                ".aac" => "audio/aac",
                ".ogg" => "audio/ogg",
                ".wma" => "audio/x-ms-wma",
                ".m4a" => "audio/mp4",
                _ => MediaTypeNames.Application.Octet
            };
        }
    }
}
