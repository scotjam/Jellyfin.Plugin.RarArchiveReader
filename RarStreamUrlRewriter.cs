using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Rewrites the host of /RarStream URLs inside PlaybackInfo responses to the host the client
    /// used for the request.
    /// <para>
    /// When a client direct-plays an HTTP .strm source, Jellyfin hands the client the literal URL
    /// from the .strm file to fetch itself. A fixed host baked into the file can never be right
    /// for every network (localhost only works for server-side ffmpeg; a LAN IP breaks remote
    /// clients). This middleware makes the URL client-agnostic: whatever address the client
    /// reached Jellyfin on is, by definition, an address it can reach — so the RarStream URL is
    /// rewritten to that same scheme and host at response time. Deployments need no address
    /// configuration at all.
    /// </para>
    /// </summary>
    public class RarStreamUrlRewriteMiddleware
    {
        // Matches the scheme://host[:port] prefix of a /RarStream/ URL, plain or JSON-escaped ("\/").
        private static readonly Regex RarStreamUrlRegex = new(
            @"https?:(\\?/){2}[^/""\\]+(?<sep>\\?/)RarStream(\\?/)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly RequestDelegate _next;
        private readonly ILogger<RarStreamUrlRewriteMiddleware> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RarStreamUrlRewriteMiddleware"/> class.
        /// </summary>
        /// <param name="next">Next middleware.</param>
        /// <param name="logger">Logger.</param>
        public RarStreamUrlRewriteMiddleware(RequestDelegate next, ILogger<RarStreamUrlRewriteMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        /// <summary>
        /// Processes the request, buffering and rewriting PlaybackInfo responses.
        /// </summary>
        /// <param name="context">HTTP context.</param>
        /// <returns>A task.</returns>
        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value;
            if (path is null || !path.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Force an uncompressed response so the body is rewritable.
            context.Request.Headers.Remove("Accept-Encoding");

            var originalBody = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await _next(context).ConfigureAwait(false);

                buffer.Position = 0;

                var contentType = context.Response.ContentType;
                var isJson = contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

                if (isJson && buffer.Length > 0 && context.Response.StatusCode == StatusCodes.Status200OK)
                {
                    var body = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);

                    if (body.Contains("RarStream", StringComparison.OrdinalIgnoreCase))
                    {
                        var rewritten = RarStreamUrlRegex.Replace(body, m =>
                        {
                            var sep = m.Groups["sep"].Value; // "/" or "\/" matching the document's escaping
                            var scheme = context.Request.Scheme;
                            var host = context.Request.Host.Value;
                            return $"{scheme}:{sep}{sep}{host}{sep}RarStream{sep}";
                        });

                        if (!ReferenceEquals(rewritten, body) && rewritten != body)
                        {
                            _logger.LogDebug(
                                "Rewrote RarStream host(s) in PlaybackInfo response to {Scheme}://{Host}",
                                context.Request.Scheme,
                                context.Request.Host.Value);

                            var outBytes = Encoding.UTF8.GetBytes(rewritten);
                            context.Response.ContentLength = outBytes.Length;
                            await originalBody.WriteAsync(outBytes).ConfigureAwait(false);
                            return;
                        }
                    }
                }

                // Pass through untouched.
                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = originalBody;
            }
        }
    }

    /// <summary>
    /// Inserts <see cref="RarStreamUrlRewriteMiddleware"/> into the ASP.NET pipeline.
    /// ASP.NET Core applies every <see cref="IStartupFilter"/> registered in DI, which is the
    /// supported way for a Jellyfin plugin to add middleware.
    /// </summary>
    public class RarStreamUrlStartupFilter : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.UseMiddleware<RarStreamUrlRewriteMiddleware>();
                next(app);
            };
        }
    }
}
