using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Oops.Web;

/// <summary>
/// Adds <see cref="IndexInjectionMiddleware"/> to Jellyfin's HTTP pipeline, so the OOPS script
/// is added to jellyfin-web's index.html without needing any other plugin.
/// </summary>
public class OopsStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<IndexInjectionMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Injects the OOPS client script into jellyfin-web's index.html as it is served.
/// </summary>
public class IndexInjectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IndexInjectionMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexInjectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next middleware.</param>
    /// <param name="logger">Logger.</param>
    public IndexInjectionMiddleware(RequestDelegate next, ILogger<IndexInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _logger.LogInformation("[OOPS] Web UI script injection is active.");
    }

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsIndexRequest(context.Request))
        {
            await _next(context);
            return;
        }

        // Ask for the plain, full file: no compression, and no 304 that would leave the browser on an uninjected copy.
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);
        context.Request.Headers.Remove(HeaderNames.IfNoneMatch);
        context.Request.Headers.Remove(HeaderNames.IfModifiedSince);

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var bytes = buffer.ToArray();
        var isHtml = context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false;
        var encoded = context.Response.Headers.ContainsKey(HeaderNames.ContentEncoding);
        if (context.Response.StatusCode == StatusCodes.Status200OK && isHtml && !encoded)
        {
            try
            {
                var html = IndexTransformer.Transform(Encoding.UTF8.GetString(bytes));
                bytes = Encoding.UTF8.GetBytes(html);
                context.Response.Headers.Remove(HeaderNames.ETag);
                context.Response.Headers.Remove(HeaderNames.LastModified);
                context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OOPS] Couldn't add the script to index.html; serving it unchanged.");
            }
        }

        context.Response.ContentLength = bytes.Length;
        await originalBody.WriteAsync(bytes);
    }

    private static bool IsIndexRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        // Matches /web/, /web/index.html and the same under a base URL such as /jellyfin/web/.
        var path = request.Path.Value ?? string.Empty;
        var idx = path.LastIndexOf("/web/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var rest = path.Substring(idx + 5);
        return rest.Length == 0 || rest.Equals("index.html", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Adds the OOPS client script to index.html.
/// </summary>
public static class IndexTransformer
{
    private const string Marker = "<!-- oops-plugin -->";

    private static readonly Lazy<string> Script = new(() =>
    {
        var assembly = typeof(IndexTransformer).Assembly;
        var name = assembly.GetManifestResourceNames().First(n => n.EndsWith("oops.js", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// Injects the OOPS client script before &lt;/body&gt;.
    /// </summary>
    /// <param name="html">The page contents.</param>
    /// <returns>The modified page.</returns>
    public static string Transform(string html)
    {
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var tag = Marker + "<script>" + Script.Value + "</script>";
        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? html + tag : html.Insert(idx, tag);
    }
}
