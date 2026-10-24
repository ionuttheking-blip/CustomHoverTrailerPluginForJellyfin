using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Jellyfin.Plugin.CustomHoverTrailer.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomHoverTrailer.Startup;

/// <summary>
/// Registers the plugin's hosted services with Jellyfin 10.11's DI container.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<WebClientInjectorHostedService>();
    }
}

/// <summary>
/// Callback handler for the File Transformation plugin (if installed) to inject our script into index.html in-memory.
/// </summary>
public static class IndexHtmlTransformationCallback
{
    private const string ScriptMarker = "id=\"custom-hover-trailer-script\"";
    private const string ScriptTag = "<script id=\"custom-hover-trailer-script\" src=\"/CustomHoverTrailer/ClientScript.js\" defer></script>";

    /// <summary>
    /// Invoked by Jellyfin.Plugin.FileTransformation when serving index.html.
    /// </summary>
    /// <param name="payload">Transformation payload containing the current HTML contents.</param>
    /// <returns>Transformed HTML string.</returns>
    public static string TransformIndexHtml(object payload)
    {
        if (payload is null)
        {
            return string.Empty;
        }

        var contentsProp = payload.GetType().GetProperty("contents", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        var currentHtml = contentsProp?.GetValue(payload) as string ?? string.Empty;

        if (string.IsNullOrEmpty(currentHtml) || currentHtml.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase))
        {
            return currentHtml;
        }

        var bodyCloseIndex = currentHtml.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyCloseIndex >= 0)
        {
            return currentHtml.Insert(bodyCloseIndex, $"{ScriptTag}\n");
        }

        return currentHtml + ScriptTag;
    }
}

/// <summary>
/// Ensures the Custom Hover Trailer client script tag is injected into Jellyfin Web's index.html at startup
/// via File Transformation (if installed) and/or direct index.html injection.
/// </summary>
public sealed class WebClientInjectorHostedService : IHostedService
{
    private const string ScriptMarker = "id=\"custom-hover-trailer-script\"";
    private const string ScriptTag = "<script id=\"custom-hover-trailer-script\" src=\"/CustomHoverTrailer/ClientScript.js\" defer></script>";

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<WebClientInjectorHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebClientInjectorHostedService"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="logger">Logger instance.</param>
    public WebClientInjectorHostedService(
        IApplicationPaths applicationPaths,
        ILogger<WebClientInjectorHostedService> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        PluginFileLogger.Info($"=== CustomHoverTrailer v1.1 starting up. Log file: {PluginFileLogger.LogFilePath} ===");
        var config = Plugin.Instance?.Configuration;
        if (config is { AutoInjectClientScript: false })
        {
            PluginFileLogger.Info("AutoInjectClientScript is disabled in configuration.");
            _logger.LogInformation("CustomHoverTrailer: AutoInjectClientScript is disabled in configuration.");
            return Task.CompletedTask;
        }

        // Method 1: Try registering with File Transformation plugin (in-memory transform, no admin disk write needed)
        TryRegisterWithFileTransformationPlugin();

        // Method 2: Also attempt direct index.html injection on disk as a fallback
        TryInjectDirectlyIntoIndexHtml();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void TryRegisterWithFileTransformationPlugin()
    {
        try
        {
            var ftAssembly = AssemblyLoadContext.All
                .SelectMany(ctx => ctx.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains("FileTransformation", StringComparison.OrdinalIgnoreCase) == true);

            if (ftAssembly is null)
            {
                return;
            }

            var pluginInterfaceType = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var registerMethod = pluginInterfaceType?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            if (registerMethod is null)
            {
                return;
            }

            var paramType = registerMethod.GetParameters().FirstOrDefault()?.ParameterType;
            var payloadJson = JsonSerializer.Serialize(new
            {
                id = Guid.Parse("9b7e4c12-6d38-4f2a-8c1e-5a9d7b3e1f41"),
                fileNamePattern = "index.html",
                callbackAssembly = GetType().Assembly.FullName,
                callbackClass = typeof(IndexHtmlTransformationCallback).FullName,
                callbackMethod = nameof(IndexHtmlTransformationCallback.TransformIndexHtml)
            });

            if (paramType is not null && paramType.FullName?.Contains("JObject", StringComparison.OrdinalIgnoreCase) == true)
            {
                var parseMethod = paramType.GetMethod("Parse", [typeof(string)]);
                if (parseMethod is not null)
                {
                    var jObjectPayload = parseMethod.Invoke(null, [payloadJson]);
                    registerMethod.Invoke(null, [jObjectPayload]);
                    _logger.LogInformation("CustomHoverTrailer: Registered index.html script injection with File Transformation plugin.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CustomHoverTrailer: File Transformation registration skipped.");
        }
    }

    private void TryInjectDirectlyIntoIndexHtml()
    {
        try
        {
            var webPath = _applicationPaths.WebPath;
            if (string.IsNullOrWhiteSpace(webPath))
            {
                return;
            }

            var indexPath = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexPath))
            {
                return;
            }

            var html = File.ReadAllText(indexPath);
            if (html.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("CustomHoverTrailer: Script tag is already present in {IndexPath}.", indexPath);
                return;
            }

            var bodyCloseIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyCloseIndex < 0)
            {
                return;
            }

            var updatedHtml = html.Insert(bodyCloseIndex, $"{ScriptTag}\n");
            File.WriteAllText(indexPath, updatedHtml);
            _logger.LogInformation("CustomHoverTrailer: Injected client script into {IndexPath}.", indexPath);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogInformation("CustomHoverTrailer: Direct write to index.html skipped (read-only); using File Transformation hook.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CustomHoverTrailer: Direct index.html injection skipped.");
        }
    }
}
