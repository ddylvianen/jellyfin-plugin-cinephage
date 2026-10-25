using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cinephage.Services;

/// <summary>
/// Adds the plugin's script tag to jellyfin-web's index.html at request time.
/// Jellyfin 12 has no script-injection hook, so this middleware rewrites the response
/// (same approach as Jellyfin Enhanced). It never writes to the web folder and serves the
/// original page unchanged on any error.
/// </summary>
public class ScriptInjectionStartupFilter : IStartupFilter
{
    private const string ScriptPath = "/Cinephage/Script";

    private readonly ILogger<ScriptInjectionStartupFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Outermost, so stripping Accept-Encoding gives an uncompressed body to rewrite.
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private static string? GetBasePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var suffix in new[] { "/web/index.html", "/web/", "/web" })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                // Everything before /web is Jellyfin's configured base URL (may be empty).
                return path[..^suffix.Length];
            }
        }

        return null;
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var basePath = GetBasePath(context.Request.Path.Value);
        if (basePath is null || !HttpMethods.IsGet(context.Request.Method))
        {
            await next().ConfigureAwait(false);
            return;
        }

        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        try
        {
            var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyClose >= 0 && html.IndexOf(ScriptPath, StringComparison.OrdinalIgnoreCase) < 0)
            {
                var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1";
                var tag = $"<script plugin=\"Cinephage\" src=\"{basePath}{ScriptPath}?v={version}\" defer></script>\n";
                html = html[..bodyClose] + tag + html[bodyClose..];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cinephage script injection failed; serving original index.html");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }
}
