using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oops.Web;

/// <summary>
/// Asks the File Transformation plugin to add the OOPS script to jellyfin-web's index.html.
/// </summary>
public class WebInjectionService : IHostedService
{
    private const string TransformationId = "206c79b6-1ae7-41b8-8974-ac834a7cf39d";

    private readonly ILogger<WebInjectionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebInjectionService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public WebInjectionService(ILogger<WebInjectionService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ftAssembly = AssemblyLoadContext.All
                .SelectMany(c => c.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);

            if (ftAssembly is null)
            {
                _logger.LogWarning("[OOPS] The File Transformation plugin isn't installed, so the 'Transfer media' menu option won't appear. Install it from https://www.iamparadox.dev/jellyfin/plugins/manifest.json and restart Jellyfin.");
                return Task.CompletedTask;
            }

            var pluginInterface = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var register = pluginInterface?.GetMethod("RegisterTransformation");
            if (register is null)
            {
                _logger.LogWarning("[OOPS] This version of File Transformation isn't compatible (RegisterTransformation not found).");
                return Task.CompletedTask;
            }

            var payloadJson = JsonSerializer.Serialize(new
            {
                id = TransformationId,
                fileNamePattern = "index\\.html$",
                callbackAssembly = typeof(IndexTransformer).Assembly.FullName,
                callbackClass = typeof(IndexTransformer).FullName,
                callbackMethod = nameof(IndexTransformer.Transform)
            });

            // File Transformation expects a Newtonsoft JObject. Build it with *its* copy of Newtonsoft
            // so the types match across plugin load contexts.
            var jObjectType = register.GetParameters()[0].ParameterType;
            var parse = jObjectType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
            if (parse is null)
            {
                _logger.LogWarning("[OOPS] Couldn't build the File Transformation payload.");
                return Task.CompletedTask;
            }

            var payload = parse.Invoke(null, new object[] { payloadJson });
            register.Invoke(null, new[] { payload });
            _logger.LogInformation("[OOPS] Registered the web UI script with File Transformation.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OOPS] Failed to register with File Transformation.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Shape of the object File Transformation passes to <see cref="IndexTransformer.Transform"/>.
/// </summary>
public class TransformPayload
{
    /// <summary>
    /// Gets or sets the current file contents.
    /// </summary>
    public string? Contents { get; set; }
}

/// <summary>
/// Called by File Transformation (via reflection) whenever index.html is served.
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
    /// <param name="payload">The file being served.</param>
    /// <returns>The modified file.</returns>
    public static string Transform(TransformPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var tag = Marker + "<script>" + Script.Value + "</script>";
        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? html + tag : html.Insert(idx, tag);
    }
}
