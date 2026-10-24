using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Reflection;
using Jellyfin.Plugin.CustomHoverTrailer.Configuration;
using Jellyfin.Plugin.CustomHoverTrailer.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Jellyfin.Plugin.CustomHoverTrailer.Controllers;

/// <summary>
/// DTO representing a Jellyfin library for the plugin configuration UI.
/// </summary>
public sealed class JellyfinLibraryDto
{
    /// <summary>
    /// Gets or sets the normalized library ItemId.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the media collection type (movies, tvshows, etc.).
    /// </summary>
    public string? CollectionType { get; set; }

    /// <summary>
    /// Gets or sets the physical locations belonging to this library.
    /// </summary>
    public List<string> Locations { get; set; } = [];
}

/// <summary>
/// Represents a single candidate trailer step in the priority chain for a movie/series item.
/// </summary>
public sealed class TrailerCandidateStepDto
{
    /// <summary>
    /// Gets or sets the source type ("LocalDisk", "RemoteMp4", or "YouTube").
    /// </summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the label/URL/filename of this candidate.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the raw YouTube URL when SourceType is "YouTube".
    /// </summary>
    public string? YouTubeUrl { get; set; }

    /// <summary>
    /// Gets or sets the direct stream URL when SourceType is "LocalDisk" or "RemoteMp4".
    /// </summary>
    public string? DirectStreamUrl { get; set; }
}

/// <summary>
/// DTO returned to the client describing the ordered candidate steps for an item so the UI can show
/// live source icons (YouTube, Local, Remote MP4) and "X" indicators when a candidate fails.
/// </summary>
public sealed class HoverTrailerInfoResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether at least one candidate trailer source exists to try.
    /// </summary>
    public bool Found { get; set; }

    /// <summary>
    /// Gets or sets the item name for console logging.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the item path on disk for console logging.
    /// </summary>
    public string? ItemPath { get; set; }

    /// <summary>
    /// Gets or sets the matched Jellyfin library name for this item.
    /// </summary>
    public string? LibraryName { get; set; }

    /// <summary>
    /// Gets or sets the active priority order used when resolving this item's trailer.
    /// </summary>
    public string? EffectivePriorityOrder { get; set; }

    /// <summary>
    /// Gets or sets the ordered list of candidate steps for the frontend to attempt sequentially.
    /// </summary>
    public List<TrailerCandidateStepDto> CandidateSteps { get; set; } = [];

    /// <summary>
    /// Gets or sets the error reason when no candidate sources exist.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the detailed server-side resolution trace so the browser console can display every step.
    /// </summary>
    public List<string> Diagnostics { get; set; } = [];
}

/// <summary>
/// Response returned when resolving a single YouTube trailer candidate URL.
/// </summary>
public sealed class YouTubeResolveAttemptResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the YouTube URL resolved to a playable video+audio stream.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the stream URL to load into the HTML5 &lt;video&gt; element.
    /// </summary>
    public string? StreamUrl { get; set; }

    /// <summary>
    /// Gets or sets whether the resolved stream is muxed or remuxed with audio.
    /// </summary>
    public bool HasAudio { get; set; }

    /// <summary>
    /// Gets or sets the error message if this YouTube candidate failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Internal cached YouTube stream entry (contains VideoUrl and optional separate AudioUrl when YouTube only provides adaptive streams).
/// </summary>
internal sealed record CachedYouTubeStreamInfo(
    string VideoUrl,
    string? AudioUrl,
    bool IsMuxedWithAudio,
    DateTimeOffset ExpiresAt);

/// <summary>
/// API controller that resolves and streams local *-trailer.mp4 files, remote .mp4 URLs, and YouTube streams (with audio!).
/// </summary>
[ApiController]
[Route("CustomHoverTrailer")]
public class HoverTrailerController : ControllerBase
{
    private static readonly string[] SupportedTrailerExtensions = [".mp4", ".webm", ".mkv", ".m4v", ".mov"];
    private static readonly string[] DefaultSources = ["LocalDisk", "RemoteMp4", "YouTube"];
    private static readonly HttpClient ProxyHttpClient = CreateProxyHttpClient();
    private static readonly YoutubeClient SharedYoutubeClient = new();
    private static readonly ConcurrentDictionary<string, CachedYouTubeStreamInfo> YouTubeStreamCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> CachedYouTubeMp4Files = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<HoverTrailerController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HoverTrailerController"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="logger">Logger instance.</param>
    public HoverTrailerController(
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths,
        ILogger<HoverTrailerController> logger)
    {
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <summary>
    /// Serves the injected client-side JavaScript that powers in-card hover previews.
    /// </summary>
    /// <returns>JavaScript content.</returns>
    [HttpGet("ClientScript.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public IActionResult GetClientScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{typeof(Plugin).Namespace}.Web.hoverTrailer.js";
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            PluginFileLogger.Error($"Embedded resource '{resourceName}' was not found in assembly.");
            return NotFound("// hoverTrailer.js resource not found");
        }

        return File(stream, "application/javascript");
    }

    /// <summary>
    /// Returns the public plugin configuration needed by the browser hover script.
    /// </summary>
    /// <returns>Plugin configuration JSON.</returns>
    [HttpGet("ClientConfig")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<PluginConfiguration> GetClientConfig()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return Ok(config);
    }

    /// <summary>
    /// Returns the list of all Jellyfin libraries (VirtualFolders) so the settings UI can configure per-library priority orders.
    /// </summary>
    /// <returns>List of Jellyfin libraries.</returns>
    [HttpGet("Libraries")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<List<JellyfinLibraryDto>> GetLibraries()
    {
        var virtualFolders = _libraryManager.GetVirtualFolders() ?? [];
        var list = virtualFolders
            .Select(vf => new JellyfinLibraryDto
            {
                Id = NormalizeId(vf.ItemId),
                Name = vf.Name ?? "Unnamed Library",
                CollectionType = vf.CollectionType?.ToString(),
                Locations = vf.Locations?.ToList() ?? []
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(list);
    }

    /// <summary>
    /// Returns the recent entries from the dedicated C# DLL log file (CustomHoverTrailer.log).
    /// </summary>
    /// <param name="lines">Number of recent lines to read.</param>
    /// <returns>Plain text log contents.</returns>
    [HttpGet("Logs")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Plain)]
    public ActionResult<string> GetDllLogs([FromQuery] int lines = 250)
    {
        return Content(PluginFileLogger.ReadRecentLogs(Math.Clamp(lines, 10, 2000)), MediaTypeNames.Text.Plain);
    }

    /// <summary>
    /// Fast endpoint that returns the ordered list of candidate trailer steps for a Jellyfin item
    /// (LocalDisk, RemoteMp4, and up to 5 YouTube URLs) in the library's exact priority order.
    /// Because it returns immediately without waiting on network calls, the frontend can show the
    /// appropriate source loading icon in the corner of the card right away and step through candidates!
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID.</param>
    /// <returns>Ordered candidate steps and diagnostics.</returns>
    [HttpGet("{itemId:guid}/Info")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<HoverTrailerInfoResponse> GetTrailerInfo([FromRoute] Guid itemId)
    {
        var diagnostics = new List<string>();
        void Trace(string msg)
        {
            diagnostics.Add(msg);
            PluginFileLogger.Info($"[Item {itemId:N}] {msg}");
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            var errMsg = $"Item {itemId} was not found in Jellyfin ILibraryManager.";
            PluginFileLogger.Warn(errMsg);
            diagnostics.Add(errMsg);
            return NotFound(new HoverTrailerInfoResponse
            {
                Found = false,
                ErrorMessage = errMsg,
                Diagnostics = diagnostics
            });
        }

        var (matchedLibraryName, priorityList) = DetermineEffectiveSourcePriority(item, config, Trace);
        var effectiveOrderStr = string.Join(" -> ", priorityList);

        Trace($"Building candidate plan for '{item.Name}' (Library='{matchedLibraryName ?? "Default"}', Priority=[{effectiveOrderStr}], Path='{item.Path ?? "null"}').");

        var remoteUrls = GetAllRemoteTrailerUrls(item);
        Trace($"Item has {remoteUrls.Count} RemoteTrailer URL(s): {(remoteUrls.Count > 0 ? string.Join(", ", remoteUrls.Take(6)) : "none")}.");

        var candidateSteps = new List<TrailerCandidateStepDto>();

        foreach (var sourceToken in priorityList)
        {
            if (string.Equals(sourceToken, "LocalDisk", StringComparison.OrdinalIgnoreCase))
            {
                if (!config.EnableLocalDiskTrailers)
                {
                    Trace("Skipping [LocalDisk] (disabled in settings).");
                    continue;
                }

                var localTrailerPath = ResolveLocalTrailerFileOnDisk(item, Trace);
                if (!string.IsNullOrEmpty(localTrailerPath))
                {
                    Trace($"[LocalDisk] Queued local trailer candidate: '{localTrailerPath}'.");
                    candidateSteps.Add(new TrailerCandidateStepDto
                    {
                        SourceType = "LocalDisk",
                        Label = Path.GetFileName(localTrailerPath),
                        DirectStreamUrl = $"/CustomHoverTrailer/{itemId:N}/Stream"
                    });
                }
                else
                {
                    Trace("[LocalDisk] No local *-trailer.mp4 file found on disk.");
                }
            }
            else if (string.Equals(sourceToken, "RemoteMp4", StringComparison.OrdinalIgnoreCase))
            {
                if (!config.EnableRemoteMp4Urls)
                {
                    Trace("Skipping [RemoteMp4] (disabled in settings).");
                    continue;
                }

                var directMp4Urls = remoteUrls.Where(IsDirectVideoUrl).Take(3).ToList();
                if (directMp4Urls.Count > 0)
                {
                    foreach (var mp4Url in directMp4Urls)
                    {
                        Trace($"[RemoteMp4] Queued direct MP4 URL candidate: '{mp4Url}'.");
                        candidateSteps.Add(new TrailerCandidateStepDto
                        {
                            SourceType = "RemoteMp4",
                            Label = mp4Url,
                            DirectStreamUrl = mp4Url
                        });
                    }
                }
                else
                {
                    Trace("[RemoteMp4] No direct .mp4/.webm URL found in RemoteTrailers.");
                }
            }
            else if (string.Equals(sourceToken, "YouTube", StringComparison.OrdinalIgnoreCase))
            {
                if (!config.EnableYouTubeFallback)
                {
                    Trace("Skipping [YouTube] (disabled in settings).");
                    continue;
                }

                var youtubeUrls = remoteUrls.Where(IsYouTubeUrl).Take(6).ToList();
                if (youtubeUrls.Count > 0)
                {
                    foreach (var ytUrl in youtubeUrls)
                    {
                        Trace($"[YouTube] Queued YouTube candidate: '{ytUrl}'.");
                        candidateSteps.Add(new TrailerCandidateStepDto
                        {
                            SourceType = "YouTube",
                            Label = ytUrl,
                            YouTubeUrl = ytUrl
                        });
                    }
                }
                else
                {
                    Trace("[YouTube] No YouTube URLs found in RemoteTrailers.");
                }
            }
        }

        if (candidateSteps.Count == 0)
        {
            var noSourceMsg = $"No trailer sources found for '{item.Name}' (checked in order: {effectiveOrderStr}).";
            Trace(noSourceMsg);
            return Ok(new HoverTrailerInfoResponse
            {
                Found = false,
                ItemName = item.Name,
                ItemPath = item.Path,
                LibraryName = matchedLibraryName,
                EffectivePriorityOrder = effectiveOrderStr,
                CandidateSteps = candidateSteps,
                ErrorMessage = noSourceMsg,
                Diagnostics = diagnostics
            });
        }

        return Ok(new HoverTrailerInfoResponse
        {
            Found = true,
            ItemName = item.Name,
            ItemPath = item.Path,
            LibraryName = matchedLibraryName,
            EffectivePriorityOrder = effectiveOrderStr,
            CandidateSteps = candidateSteps,
            Diagnostics = diagnostics
        });
    }

    /// <summary>
    /// Resolves a single YouTube candidate URL via YoutubeExplode so the frontend can display the YouTube searching icon
    /// and flash an 'X' if a specific YouTube trailer fails before trying the next candidate.
    /// Also ensures AUDIO is present (remuxing video+audio with ffmpeg if YouTube only returned adaptive video-only streams!).
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID.</param>
    /// <param name="url">The specific YouTube URL to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolution result for this YouTube URL.</returns>
    [HttpGet("{itemId:guid}/ResolveYouTube")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<YouTubeResolveAttemptResponse>> ResolveSingleYouTubeCandidate(
        [FromRoute] Guid itemId,
        [FromQuery] string url,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsYouTubeUrl(url))
        {
            return BadRequest(new YouTubeResolveAttemptResponse
            {
                Success = false,
                ErrorMessage = "Invalid YouTube URL."
            });
        }

        PluginFileLogger.Info($"[ResolveYouTube {itemId:N}] Resolving YouTube candidate '{url}'...");
        var (streamInfo, error) = await ResolveYouTubeStreamInfoWithAudioAsync(
            url,
            msg => PluginFileLogger.Info($"[ResolveYouTube {itemId:N}] {msg}"),
            cancellationToken);

        if (streamInfo is null)
        {
            PluginFileLogger.Warn($"[ResolveYouTube {itemId:N}] Candidate '{url}' failed: {error}");
            return Ok(new YouTubeResolveAttemptResponse
            {
                Success = false,
                ErrorMessage = error ?? "Failed to resolve YouTube stream."
            });
        }

        var proxyUrl = $"/CustomHoverTrailer/{itemId:N}/YouTubeStream?url={Uri.EscapeDataString(url)}";
        return Ok(new YouTubeResolveAttemptResponse
        {
            Success = true,
            StreamUrl = proxyUrl,
            HasAudio = true
        });
    }

    /// <summary>
    /// Streams the local *name_of_video*-trailer.mp4 file directly from disk with HTTP Range support.
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID.</param>
    /// <returns>File stream result with range processing enabled.</returns>
    [HttpGet("{itemId:guid}/Stream")]
    [AllowAnonymous]
    public IActionResult StreamLocalTrailer([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            PluginFileLogger.Warn($"[Stream] Item {itemId} not found.");
            return NotFound("Item not found.");
        }

        var localTrailerPath = ResolveLocalTrailerFileOnDisk(item, null);
        if (string.IsNullOrEmpty(localTrailerPath) || !System.IO.File.Exists(localTrailerPath))
        {
            PluginFileLogger.Warn($"[Stream] Local trailer file not found on disk for '{item.Name}' ({itemId}).");
            return NotFound("Local trailer file not found on disk.");
        }

        PluginFileLogger.Info($"[Stream] Serving local trailer '{localTrailerPath}' for '{item.Name}'.");
        var contentType = GetMimeTypeForExtension(Path.GetExtension(localTrailerPath));
        return PhysicalFile(localTrailerPath, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Streams the YouTube trailer with full Video + Audio!
    /// - If YouTube provided a muxed (video+audio) stream, proxies it with HTTP Range support.
    /// - If YouTube only provided separate video-only and audio-only streams (the reason sound was missing before!),
    ///   uses ffmpeg to fast-mux the first 35 seconds of video + audio into a seekable MP4 file (cached in temp) and streams it with Range support!
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID.</param>
    /// <param name="url">The specific YouTube URL to stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Streamed video+audio response.</returns>
    [HttpGet("{itemId:guid}/YouTubeStream")]
    [AllowAnonymous]
    public async Task<IActionResult> StreamYouTubeTrailer(
        [FromRoute] Guid itemId,
        [FromQuery] string? url,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound("Item not found.");
        }

        var youtubeUrl = !string.IsNullOrWhiteSpace(url) && IsYouTubeUrl(url)
            ? url
            : GetAllRemoteTrailerUrls(item).FirstOrDefault(IsYouTubeUrl);

        if (string.IsNullOrEmpty(youtubeUrl))
        {
            return NotFound("No YouTube URL on item.");
        }

        // Check if we already have a cached muxed MP4 file on disk for this YouTube URL
        if (CachedYouTubeMp4Files.TryGetValue(youtubeUrl, out var existingCacheFile) && System.IO.File.Exists(existingCacheFile))
        {
            PluginFileLogger.Info($"[YouTubeStream {itemId:N}] Serving cached muxed YouTube MP4 with audio: '{existingCacheFile}'.");
            return PhysicalFile(existingCacheFile, "video/mp4", enableRangeProcessing: true);
        }

        var (streamInfo, error) = await ResolveYouTubeStreamInfoWithAudioAsync(
            youtubeUrl,
            msg => PluginFileLogger.Info($"[YouTubeStream {itemId:N}] {msg}"),
            cancellationToken);

        if (streamInfo is null)
        {
            PluginFileLogger.Error($"[YouTubeStream {itemId:N}] Failed resolving YouTube stream for '{youtubeUrl}': {error}");
            return StatusCode(502, $"YoutubeExplode failed to resolve stream: {error}");
        }

        // Case 1: YouTube only gave separate video-only + audio-only streams -> fast-mux 35s with ffmpeg so sound ALWAYS works!
        if (!streamInfo.IsMuxedWithAudio && !string.IsNullOrEmpty(streamInfo.AudioUrl))
        {
            var muxedFile = await EnsureMuxedYouTubePreviewFileAsync(youtubeUrl, streamInfo.VideoUrl, streamInfo.AudioUrl, cancellationToken);
            if (!string.IsNullOrEmpty(muxedFile) && System.IO.File.Exists(muxedFile))
            {
                return PhysicalFile(muxedFile, "video/mp4", enableRangeProcessing: true);
            }
        }

        // Case 2: Stream is already muxed (contains both video + audio) -> proxy directly with Range support
        try
        {
            using var upstreamRequest = new HttpRequestMessage(HttpMethod.Get, streamInfo.VideoUrl);
            if (Request.Headers.TryGetValue("Range", out var rangeHeader) &&
                AuthenticationHeaderValue.TryParse(rangeHeader.ToString(), out _) == false)
            {
                upstreamRequest.Headers.TryAddWithoutValidation("Range", rangeHeader.ToString());
            }

            var upstreamResponse = await ProxyHttpClient.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!upstreamResponse.IsSuccessStatusCode)
            {
                YouTubeStreamCache.TryRemove(youtubeUrl, out _);
                PluginFileLogger.Warn($"[YouTubeStream {itemId:N}] Upstream googlevideo returned HTTP {(int)upstreamResponse.StatusCode}.");
                upstreamResponse.Dispose();
                return StatusCode((int)upstreamResponse.StatusCode, "Upstream YouTube stream returned an error.");
            }

            Response.StatusCode = (int)upstreamResponse.StatusCode;
            Response.Headers["Accept-Ranges"] = "bytes";

            if (upstreamResponse.Content.Headers.ContentLength.HasValue)
            {
                Response.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;
            }

            if (upstreamResponse.Content.Headers.ContentRange is not null)
            {
                Response.Headers["Content-Range"] = upstreamResponse.Content.Headers.ContentRange.ToString();
            }

            var contentType = upstreamResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4";
            Response.ContentType = contentType;

            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            await upstreamStream.CopyToAsync(Response.Body, 64 * 1024, cancellationToken);
            upstreamResponse.Dispose();
            return new EmptyResult();
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            PluginFileLogger.Error($"[YouTubeStream {itemId:N}] Error proxying YouTube stream for '{item.Name}'", ex);
            return StatusCode(500, ex.Message);
        }
    }

    /// <summary>
    /// Resolves a YouTube URL via YoutubeExplode.
    /// Checks for a muxed (video+audio) stream first; if YouTube only returns adaptive streams (video-only + audio-only),
    /// captures BOTH the video stream URL and the AAC/MP4 audio stream URL so we can mux them with audio!
    /// </summary>
    private static async Task<(CachedYouTubeStreamInfo? Info, string? ErrorMessage)> ResolveYouTubeStreamInfoWithAudioAsync(
        string youtubeUrl,
        Action<string>? trace,
        CancellationToken cancellationToken)
    {
        if (YouTubeStreamCache.TryGetValue(youtubeUrl, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            trace?.Invoke($"Found valid cached YouTube stream (IsMuxedWithAudio={cached.IsMuxedWithAudio}).");
            return (cached, null);
        }

        try
        {
            var manifest = await SharedYoutubeClient.Videos.Streams.GetManifestAsync(youtubeUrl, cancellationToken);

            // 1. Check if YouTube returned a true muxed stream (video + audio together)
            var muxedStream = manifest
                .GetMuxedStreams()
                .OrderByDescending(s => s.Container == Container.Mp4)
                .ThenByDescending(s => s.VideoQuality.MaxHeight <= 720)
                .ThenByDescending(s => s.VideoQuality.MaxHeight)
                .FirstOrDefault();

            if (muxedStream is not null && !string.IsNullOrWhiteSpace(muxedStream.Url))
            {
                trace?.Invoke($"YoutubeExplode selected MUXED (video+audio) stream: Container={muxedStream.Container}, Quality={muxedStream.VideoQuality.Label}, AudioCodec={muxedStream.AudioCodec}.");
                var info = new CachedYouTubeStreamInfo(
                    VideoUrl: muxedStream.Url,
                    AudioUrl: null,
                    IsMuxedWithAudio: true,
                    ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(40));

                YouTubeStreamCache[youtubeUrl] = info;
                return (info, null);
            }

            // 2. YouTube did not return any muxed streams (very common in 2026 InnerTube responses!).
            // Select the best H.264/MP4 video stream <= 720p AND the best MP4/M4A (AAC) audio stream so we can remux them with audio!
            var videoStream = manifest
                .GetVideoOnlyStreams()
                .Where(s => s.Container == Container.Mp4 || s.Container == Container.WebM)
                .OrderByDescending(s => s.Container == Container.Mp4)
                .ThenByDescending(s => s.VideoCodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(s => s.VideoQuality.MaxHeight <= 720)
                .ThenByDescending(s => s.VideoQuality.MaxHeight)
                .FirstOrDefault();

            var audioStream = manifest
                .GetAudioOnlyStreams()
                .OrderByDescending(s => s.Container == Container.Mp4)
                .ThenByDescending(s => s.AudioCodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(s => s.Bitrate)
                .FirstOrDefault();

            if (videoStream is null || string.IsNullOrWhiteSpace(videoStream.Url))
            {
                return (null, "YoutubeExplode returned manifest, but no compatible video stream was available.");
            }

            trace?.Invoke($"YoutubeExplode selected adaptive Video={videoStream.VideoQuality.Label} ({videoStream.VideoCodec}) + Audio={(audioStream is not null ? audioStream.AudioCodec : "none")}.");

            var adaptiveInfo = new CachedYouTubeStreamInfo(
                VideoUrl: videoStream.Url,
                AudioUrl: audioStream?.Url,
                IsMuxedWithAudio: false,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(40));

            YouTubeStreamCache[youtubeUrl] = adaptiveInfo;
            return (adaptiveInfo, null);
        }
        catch (Exception ex)
        {
            PluginFileLogger.Error($"YoutubeExplode exception for '{youtubeUrl}'", ex);
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Uses Jellyfin's ffmpeg to rapidly mux the first N seconds of separate YouTube video + audio streams
    /// using stream copy (-c:v copy -c:a aac) so the browser plays video WITH SOUND and instant seeking on TV/Phone/PC!
    /// </summary>
    private async Task<string?> EnsureMuxedYouTubePreviewFileAsync(
        string youtubeUrl,
        string videoStreamUrl,
        string audioStreamUrl,
        CancellationToken cancellationToken)
    {
        if (CachedYouTubeMp4Files.TryGetValue(youtubeUrl, out var existing) && System.IO.File.Exists(existing))
        {
            return existing;
        }

        var cacheDir = Path.Combine(_applicationPaths.TempDirectory, "CustomHoverTrailerCache");
        Directory.CreateDirectory(cacheDir);

        var safeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(youtubeUrl)))[..16];
        var finalPath = Path.Combine(cacheDir, $"yt_{safeHash}.mp4");
        if (System.IO.File.Exists(finalPath) && new FileInfo(finalPath).Length > 10_000)
        {
            CachedYouTubeMp4Files[youtubeUrl] = finalPath;
            return finalPath;
        }

        var tempPath = Path.Combine(cacheDir, $"yt_{safeHash}_{Guid.NewGuid():N}.tmp.mp4");
        var maxDuration = Math.Clamp((Plugin.Instance?.Configuration?.MaxPreviewDurationSeconds ?? 30) + 5, 15, 90);
        var ffmpegExe = FindJellyfinFfmpegPath();

        try
        {
            // -c:v copy avoids video re-encoding so muxing 30s takes ~1 second!
            var args =
                $"-hide_banner -loglevel error -y " +
                $"-t {maxDuration} -i \"{videoStreamUrl}\" " +
                $"-t {maxDuration} -i \"{audioStreamUrl}\" " +
                $"-map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 128k -ac 2 -shortest -movflags +faststart \"{tempPath}\"";

            PluginFileLogger.Info($"[FFmpeg Mux] Muxing YouTube video+audio ({maxDuration}s) for '{youtubeUrl}' using '{ffmpegExe}'...");

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;

            if (process.ExitCode == 0 && System.IO.File.Exists(tempPath) && new FileInfo(tempPath).Length > 10_000)
            {
                if (System.IO.File.Exists(finalPath))
                {
                    System.IO.File.Delete(finalPath);
                }

                System.IO.File.Move(tempPath, finalPath);
                CachedYouTubeMp4Files[youtubeUrl] = finalPath;
                PluginFileLogger.Info($"[FFmpeg Mux] Successfully muxed YouTube video+audio to '{finalPath}'.");
                return finalPath;
            }

            PluginFileLogger.Warn($"[FFmpeg Mux] Failed muxing YouTube streams (exit {process.ExitCode}): {stderr.Trim()}");
        }
        catch (Exception ex)
        {
            PluginFileLogger.Error($"[FFmpeg Mux] Exception muxing YouTube video+audio for '{youtubeUrl}'", ex);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
            {
                try
                {
                    System.IO.File.Delete(tempPath);
                }
                catch
                {
                    // Ignore cleanup error
                }
            }
        }

        return null;
    }

    private static string FindJellyfinFfmpegPath()
    {
        string[] candidates =
        [
            @"C:\Program Files\Jellyfin\Server\ffmpeg.exe",
            @"C:\ProgramData\Jellyfin\Server\ffmpeg.exe",
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe"
        ];

        foreach (var c in candidates)
        {
            if (System.IO.File.Exists(c))
            {
                return c;
            }
        }

        return "ffmpeg.exe";
    }

    private (string? LibraryName, List<string> PriorityOrder) DetermineEffectiveSourcePriority(
        BaseItem item,
        PluginConfiguration config,
        Action<string>? trace)
    {
        string? matchedLibId = null;
        string? matchedLibName = null;

        try
        {
            var virtualFolders = _libraryManager.GetVirtualFolders() ?? [];
            var itemPath = item.Path;

            if (!string.IsNullOrWhiteSpace(itemPath))
            {
                foreach (var vf in virtualFolders)
                {
                    if (vf.Locations is null)
                    {
                        continue;
                    }

                    foreach (var loc in vf.Locations)
                    {
                        if (!string.IsNullOrWhiteSpace(loc) &&
                            itemPath.StartsWith(loc, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedLibId = NormalizeId(vf.ItemId);
                            matchedLibName = vf.Name;
                            break;
                        }
                    }

                    if (matchedLibId is not null)
                    {
                        break;
                    }
                }
            }

            if (matchedLibId is null)
            {
                var collectionFolders = _libraryManager.GetCollectionFolders(item) ?? [];
                var firstFolder = collectionFolders.FirstOrDefault();
                if (firstFolder is not null)
                {
                    matchedLibId = NormalizeId(firstFolder.Id.ToString("N"));
                    matchedLibName = firstFolder.Name;
                }
            }
        }
        catch (Exception ex)
        {
            PluginFileLogger.Error($"Error determining library for item '{item.Name}'", ex);
        }

        if (!string.IsNullOrEmpty(matchedLibId) || !string.IsNullOrEmpty(matchedLibName))
        {
            var libOverride = config.LibraryPriorityOverrides?.FirstOrDefault(o =>
                o.UseCustomPriority &&
                ((!string.IsNullOrEmpty(o.LibraryId) && string.Equals(NormalizeId(o.LibraryId), matchedLibId, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrEmpty(o.LibraryName) && string.Equals(o.LibraryName, matchedLibName, StringComparison.OrdinalIgnoreCase))));

            if (libOverride is not null)
            {
                var customOrder = ParsePriorityOrder(libOverride.SourcePriorityOrder);
                trace?.Invoke($"Using custom priority override for library '{matchedLibName}': {string.Join(" -> ", customOrder)}.");
                return (matchedLibName, customOrder);
            }
        }

        var defaultOrder = ParsePriorityOrder(config.DefaultSourcePriorityOrder);
        return (matchedLibName, defaultOrder);
    }

    private static List<string> ParsePriorityOrder(string? rawOrder)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(rawOrder))
        {
            var parts = rawOrder.Split([',', ';', '>', ' '], StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Equals("LocalDisk", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals("Local", StringComparison.OrdinalIgnoreCase))
                {
                    if (!result.Contains("LocalDisk", StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add("LocalDisk");
                    }
                }
                else if (trimmed.Equals("RemoteMp4", StringComparison.OrdinalIgnoreCase) ||
                         trimmed.Equals("Mp4Url", StringComparison.OrdinalIgnoreCase))
                {
                    if (!result.Contains("RemoteMp4", StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add("RemoteMp4");
                    }
                }
                else if (trimmed.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ||
                         trimmed.Equals("YouTubeStream", StringComparison.OrdinalIgnoreCase))
                {
                    if (!result.Contains("YouTube", StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add("YouTube");
                    }
                }
            }
        }

        foreach (var fallback in DefaultSources)
        {
            if (!result.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(fallback);
            }
        }

        return result;
    }

    private static string NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        return id.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
    }

    private string? ResolveLocalTrailerFileOnDisk(BaseItem item, Action<string>? trace)
    {
        try
        {
            var itemPath = item.Path;
            if (string.IsNullOrWhiteSpace(itemPath))
            {
                trace?.Invoke("Item.Path is empty.");
                return null;
            }

            if (System.IO.File.Exists(itemPath))
            {
                var directory = Path.GetDirectoryName(itemPath);
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(itemPath);
                if (!string.IsNullOrEmpty(directory) && !string.IsNullOrEmpty(fileNameWithoutExt))
                {
                    var primaryExpected = Path.Combine(directory, $"{fileNameWithoutExt}-trailer.mp4");
                    trace?.Invoke($"Checking primary local trailer path: '{primaryExpected}'.");

                    foreach (var ext in SupportedTrailerExtensions)
                    {
                        var candidate = Path.Combine(directory, $"{fileNameWithoutExt}-trailer{ext}");
                        if (System.IO.File.Exists(candidate))
                        {
                            return candidate;
                        }

                        var underscoreCandidate = Path.Combine(directory, $"{fileNameWithoutExt}_trailer{ext}");
                        if (System.IO.File.Exists(underscoreCandidate))
                        {
                            return underscoreCandidate;
                        }
                    }

                    var expectedPrefix = $"{fileNameWithoutExt}-trailer";
                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        var ext = Path.GetExtension(file);
                        if (!SupportedTrailerExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var candidateNameNoExt = Path.GetFileNameWithoutExtension(file);
                        if (string.Equals(candidateNameNoExt, expectedPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            return file;
                        }
                    }
                }
            }

            if (Directory.Exists(itemPath))
            {
                var folderName = Path.GetFileName(itemPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                trace?.Invoke($"Item is a directory ('{itemPath}'). Checking '{folderName}-trailer.mp4'...");
                foreach (var ext in SupportedTrailerExtensions)
                {
                    var candidate = Path.Combine(itemPath, $"{folderName}-trailer{ext}");
                    if (System.IO.File.Exists(candidate))
                    {
                        return candidate;
                    }

                    var genericCandidate = Path.Combine(itemPath, $"trailer{ext}");
                    if (System.IO.File.Exists(genericCandidate))
                    {
                        return genericCandidate;
                    }
                }

                if (item is Series series)
                {
                    var firstEpisode = series.GetRecursiveChildren().OfType<Episode>().FirstOrDefault();
                    if (firstEpisode is not null)
                    {
                        return ResolveLocalTrailerFileOnDisk(firstEpisode, trace);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking local disk trailer for item {ItemId} ({ItemName})", item.Id, item.Name);
            PluginFileLogger.Error($"Error checking local disk trailer for '{item.Name}' ({item.Id})", ex);
        }

        return null;
    }

    private static List<string> GetAllRemoteTrailerUrls(BaseItem item)
    {
        var urls = new List<string>();
        if (item.RemoteTrailers is { Count: > 0 })
        {
            foreach (var trailer in item.RemoteTrailers)
            {
                if (!string.IsNullOrWhiteSpace(trailer?.Url))
                {
                    urls.Add(trailer.Url.Trim());
                }
            }
        }

        return urls;
    }

    private static bool IsDirectVideoUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (IsYouTubeUrl(url))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
               url.Contains(".mp4?", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("mime=video%2Fmp4", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("mime=video/mp4", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsYouTubeUrl(string url)
    {
        return url.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateProxyHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        return client;
    }

    private static string GetMimeTypeForExtension(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream"
        };
    }
}
