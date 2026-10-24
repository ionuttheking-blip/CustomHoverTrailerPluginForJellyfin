using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using TrailerGenerator.WinForms.Models;

namespace TrailerGenerator.WinForms.Services;

/// <summary>
/// The 3 available trailer generation modes.
/// </summary>
public enum TrailerGenerationMode
{
    Random = 0,            // 1) RANDOM: split usable video into scenes and pick a random moment from each scene
    SoundDetect = 1,       // 2) SOUND DETECT: split usable video into scenes, get loudest moment per scene, center loud sound in middle of sequence
    MovementDetection = 2  // 3) MOVEMENT DETECTION: split usable video into scenes, get moment with most movement per scene, center movement in middle of sequence
}

/// <summary>
/// Options for generating local *-trailer.mp4 preview clips.
/// </summary>
public sealed class TrailerGenerationOptions
{
    public string FfmpegPath { get; set; } = string.Empty;
    public TrailerGenerationMode Mode { get; set; } = TrailerGenerationMode.Random;
    public int TargetDurationSeconds { get; set; } = 60;
    public int SnippetCount { get; set; } = 6;
    public bool IncludeAudio { get; set; } = true;
    public bool OverwriteExisting { get; set; } = false;
    public bool SmoothFadeTransitions { get; set; } = true;
    public string ResolutionScale { get; set; } = "1280:-2";
    public bool UsePercentageOffset { get; set; } = true;
    public double StartPercentage { get; set; } = 10.0;
    public double FixedStartSeconds { get; set; } = 120.0;
    public double EndSkipPercentage { get; set; } = 10.0;
    public int MaxConcurrentProcesses { get; set; } = 2;
    public bool UseSceneChangeDetection { get; set; } = false;
    public List<ManualSegmentItem>? ManualSegments { get; set; }

    // Backward-compatibility aliases
    public int ClipDurationSeconds
    {
        get => TargetDurationSeconds;
        set => TargetDurationSeconds = value;
    }

    public bool SmoothSceneTransitions
    {
        get => SmoothFadeTransitions;
        set => SmoothFadeTransitions = value;
    }

    public int MaxHeightResolution { get; set; } = 720;
}

/// <summary>
/// Detailed result of analyzing a video timeline, containing scenes, detected scene cuts,
/// and computed candidate segments for Audio, Movement, and Random modes.
/// </summary>
public sealed class VideoTimelineAnalysisResult
{
    public double DurationSeconds { get; set; }
    public double UsableStartSeconds { get; set; }
    public double UsableEndSeconds { get; set; }
    public List<(double SceneStart, double SceneEnd)> Scenes { get; set; } = new();
    public List<double> SceneCutTimestamps { get; set; } = new();
    public List<ManualSegmentItem> AudioModeSegments { get; set; } = new();
    public List<ManualSegmentItem> MovementModeSegments { get; set; } = new();
    public List<ManualSegmentItem> RandomModeSegments { get; set; } = new();
    public List<(double AbsoluteTimestamp, double SceneScore)>? MotionPoints { get; set; }
    public List<(double AbsoluteTimestamp, double RmsDb)>? AudioPoints { get; set; }
}

/// <summary>
/// Wraps ffmpeg and ffprobe to generate *-trailer.mp4 clips in the same directory as the source video
/// supporting 3 scene-based generation modes: Random, Sound Detect, and Movement Detection.
/// </summary>
public sealed class FfmpegGeneratorService
{
    /// <summary>
    /// Attempts to auto-detect ffmpeg.exe from Jellyfin's install directory, common locations, or PATH.
    /// </summary>
    public static string FindDefaultFfmpegPath()
    {
        string[] candidatePaths =
        [
            @"C:\Program Files\Jellyfin\Server\ffmpeg.exe",
            @"C:\ProgramData\Jellyfin\Server\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Jellyfin\Server\ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe"
        ];

        foreach (var candidate in candidatePaths)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var fullPath = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
                // Ignore invalid PATH entries
            }
        }

        return "ffmpeg.exe";
    }

    /// <summary>
    /// Probes the total duration of a video file in seconds using ffprobe (or ffmpeg fallback).
    /// </summary>
    public async Task<double?> GetVideoDurationSecondsAsync(string ffmpegPath, string videoPath, CancellationToken cancellationToken)
    {
        var ffprobePath = GetSiblingFfprobePath(ffmpegPath);
        if (File.Exists(ffprobePath))
        {
            var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";
            var (exitCode, stdout, _) = await RunProcessAsync(ffprobePath, args, cancellationToken);
            if (exitCode == 0 && double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                return seconds;
            }
        }

        var (_, _, stderr) = await RunProcessAsync(ffmpegPath, $"-hide_banner -i \"{videoPath}\"", cancellationToken);
        var match = Regex.Match(stderr, @"Duration:\s*(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)");
        if (match.Success)
        {
            var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var secs = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            return (hours * 3600) + (minutes * 60) + secs;
        }

        return null;
    }

    /// <summary>
    /// Alias for GetVideoDurationSecondsAsync.
    /// </summary>
    public Task<double?> GetVideoDurationAsync(string ffmpegPath, string videoPath, CancellationToken cancellationToken)
        => GetVideoDurationSecondsAsync(ffmpegPath, videoPath, cancellationToken);

    /// <summary>
    /// Computes a unique cache key for the given trailer options.
    /// </summary>
    public static string GetConfigurationKey(TrailerGenerationOptions options)
    {
        string offsetStr = options.UsePercentageOffset
            ? FormattableString.Invariant($"Pct_{options.StartPercentage:F1}")
            : FormattableString.Invariant($"Sec_{options.FixedStartSeconds:F0}");
        return FormattableString.Invariant($"Mode_{options.Mode}_Dur_{options.TargetDurationSeconds}_Snip_{options.SnippetCount}_SceneChg_{options.UseSceneChangeDetection}_{offsetStr}_End_{options.EndSkipPercentage:F1}");
    }

    /// <summary>
    /// Generates the *name_of_video*-trailer.mp4 file in the same folder as the video using the chosen mode.
    /// This 5-parameter overload forwards to the 6-parameter overload passing videoEntry: null for backward compatibility.
    /// </summary>
    public Task GenerateTrailerAsync(
        string videoPath,
        string outputTrailerPath,
        TrailerGenerationOptions options,
        Action<string>? logCallback,
        CancellationToken cancellationToken)
        => GenerateTrailerAsync(videoPath, outputTrailerPath, options, logCallback, cancellationToken, videoEntry: null);

    /// <summary>
    /// Generates the *name_of_video*-trailer.mp4 file in the same folder as the video using the chosen mode,
    /// supporting saved configuration re-use and cached analysis via the provided <paramref name="videoEntry"/>.
    /// This method is fully thread-safe and can be invoked concurrently on multiple videos.
    /// </summary>
    public async Task GenerateTrailerAsync(
        string videoPath,
        string outputTrailerPath,
        TrailerGenerationOptions options,
        Action<string>? logCallback,
        CancellationToken cancellationToken,
        VideoEntry? videoEntry = null)
    {
        if (!options.OverwriteExisting && File.Exists(outputTrailerPath))
        {
            logCallback?.Invoke($"Skipping '{Path.GetFileName(videoPath)}' (trailer already exists).");
            return;
        }

        var ffmpegPath = string.IsNullOrWhiteSpace(options.FfmpegPath) ? FindDefaultFfmpegPath() : options.FfmpegPath;
        var duration = await GetVideoDurationAsync(ffmpegPath, videoPath, cancellationToken);
        if (!duration.HasValue || duration.Value <= 0)
        {
            throw new InvalidOperationException($"Could not determine duration for video: '{Path.GetFileName(videoPath)}'.");
        }

        List<(double StartSeconds, double DurationSeconds)> segments;

        if (options.ManualSegments is { Count: > 0 })
        {
            logCallback?.Invoke($"Using {options.ManualSegments.Count} manually configured trailer segment(s)...");
            segments = new List<(double StartSeconds, double DurationSeconds)>(options.ManualSegments.Count);
            foreach (var seg in options.ManualSegments)
            {
                double durSec = Math.Max(0.5, seg.DurationSeconds);
                if (durSec > duration.Value)
                {
                    durSec = Math.Max(0.5, duration.Value);
                }
                double maxStart = Math.Max(0.0, duration.Value - durSec);
                double startSec = Math.Clamp(seg.StartSeconds, 0.0, maxStart);
                segments.Add((startSec, durSec));
            }
        }
        else if (options.Mode != TrailerGenerationMode.Random && videoEntry is { } entry)
        {
            string configKey = GetConfigurationKey(options);
            List<ManualSegmentItem>? savedSegments = null;
            lock (entry.SavedConfigurations)
            {
                if (entry.SavedConfigurations.TryGetValue(configKey, out var found) && found is { Count: > 0 })
                {
                    savedSegments = found.Select(s => s.Clone()).ToList();
                }
            }

            if (savedSegments is { Count: > 0 })
            {
                logCallback?.Invoke($"Re-using existing configuration '{configKey}' ({savedSegments.Count} segments) for '{Path.GetFileName(videoPath)}' (skipping analysis)...");
                segments = new List<(double StartSeconds, double DurationSeconds)>(savedSegments.Count);
                foreach (var seg in savedSegments)
                {
                    double durSec = Math.Max(0.5, seg.DurationSeconds);
                    if (durSec > duration.Value)
                    {
                        durSec = Math.Max(0.5, duration.Value);
                    }
                    double maxStart = Math.Max(0.0, duration.Value - durSec);
                    double startSec = Math.Clamp(seg.StartSeconds, 0.0, maxStart);
                    segments.Add((startSec, durSec));
                }
            }
            else
            {
                segments = await BuildSegmentsForModeAsync(ffmpegPath, videoPath, duration.Value, options, logCallback, cancellationToken, entry.CachedAnalysis);
                lock (entry.SavedConfigurations)
                {
                    entry.SavedConfigurations[configKey] = segments
                        .Select(s => new ManualSegmentItem { StartSeconds = s.StartSeconds, DurationSeconds = s.DurationSeconds })
                        .ToList();
                }
                logCallback?.Invoke($"Saved configuration [{configKey}] for '{Path.GetFileName(videoPath)}'.");
            }
        }
        else
        {
            segments = await BuildSegmentsForModeAsync(ffmpegPath, videoPath, duration.Value, options, logCallback, cancellationToken, videoEntry?.CachedAnalysis);
        }

        await ExtractAndConcatSegmentsAsync(ffmpegPath, videoPath, outputTrailerPath, segments, options, logCallback, cancellationToken);
        string modeLabel = options.ManualSegments is { Count: > 0 } ? "Manual" : options.Mode.ToString();
        logCallback?.Invoke($"Created [{modeLabel}]: {outputTrailerPath}");
    }

    /// <summary>
    /// Analyzes a video file's timeline across its usable span.
    /// This 5-parameter overload forwards to the 7-parameter overload passing videoEntry: null, forceReanalyze: false for backward compatibility.
    /// </summary>
    public Task<VideoTimelineAnalysisResult> AnalyzeVideoTimelineAsync(
        string ffmpegPath,
        string videoPath,
        TrailerGenerationOptions options,
        Action<string>? logCallback,
        CancellationToken cancellationToken)
        => AnalyzeVideoTimelineAsync(ffmpegPath, videoPath, options, logCallback, cancellationToken, videoEntry: null, forceReanalyze: false);

    /// <summary>
    /// Analyzes a video file's timeline across its usable span, identifying scene cuts,
    /// audio peaks, and visual motion to generate pre-calculated segment lists for
    /// Audio, Movement, and Random modes. Supports cached analysis and forced reanalysis.
    /// </summary>
    public async Task<VideoTimelineAnalysisResult> AnalyzeVideoTimelineAsync(
        string ffmpegPath,
        string videoPath,
        TrailerGenerationOptions options,
        Action<string>? logCallback,
        CancellationToken cancellationToken,
        VideoEntry? videoEntry = null,
        bool forceReanalyze = false)
    {
        if (!forceReanalyze && videoEntry?.CachedAnalysis != null)
        {
            logCallback?.Invoke($"Using cached timeline analysis for '{Path.GetFileName(videoPath)}' (instant)...");
            return videoEntry.CachedAnalysis;
        }

        var binary = string.IsNullOrWhiteSpace(ffmpegPath) ? FindDefaultFfmpegPath() : ffmpegPath;
        var duration = await GetVideoDurationAsync(binary, videoPath, cancellationToken);
        if (!duration.HasValue || duration.Value <= 0)
        {
            throw new InvalidOperationException($"Could not determine duration for video: '{Path.GetFileName(videoPath)}'.");
        }

        var totalEffectiveDuration = Math.Max(5, options.TargetDurationSeconds);
        var snippetCount = Math.Clamp(options.SnippetCount, 1, 30);

        // Start & End Skip Calculation (Applied to ALL 3 Modes)
        double startSkip;
        if (options.UsePercentageOffset)
        {
            double pct = Math.Clamp(options.StartPercentage, 0.0, 45.0) / 100.0;
            startSkip = duration.Value * pct;
        }
        else
        {
            startSkip = Math.Clamp(options.FixedStartSeconds, 0.0, duration.Value * 0.45);
        }

        double endPct = Math.Clamp(options.EndSkipPercentage, 0.0, 45.0) / 100.0;
        double endSkip = duration.Value * endPct;

        double usableStart = Math.Clamp(startSkip, 0.0, duration.Value * 0.85);
        double usableEnd = Math.Clamp(duration.Value - endSkip, usableStart + 5.0, duration.Value);

        double usableDuration = usableEnd - usableStart;
        if (usableDuration < totalEffectiveDuration + 2.0)
        {
            usableStart = 0.0;
            usableEnd = duration.Value;
            usableDuration = usableEnd - usableStart;
        }

        logCallback?.Invoke("Analyzing video movement & scene cuts...");
        var motionPoints = await AnalyzeMovementAcrossUsableSpanAsync(
            binary,
            videoPath,
            usableStart,
            usableDuration,
            cancellationToken);

        logCallback?.Invoke("Analyzing audio loudness across scenes...");
        var audioPoints = await AnalyzeAudioRmsAcrossUsableSpanAsync(
            binary,
            videoPath,
            usableStart,
            usableDuration,
            cancellationToken);

        // Always compute detected scenes so the Detected Scene Changes Bar displays actual scene boundaries and cuts
        var detectedScenes = BuildScenesFromSceneChanges(
            motionPoints,
            usableStart,
            usableEnd,
            totalEffectiveDuration,
            snippetCount);

        var sceneCutTimestamps = detectedScenes.Skip(1).Select(s => s.SceneStart).ToList();

        // Active scenes for mode calculation:
        List<(double SceneStart, double SceneEnd)> activeScenes;
        if (options.UseSceneChangeDetection)
        {
            activeScenes = detectedScenes;
        }
        else
        {
            activeScenes = new List<(double SceneStart, double SceneEnd)>(snippetCount);
            double sceneDuration = usableDuration / snippetCount;
            for (var i = 0; i < snippetCount; i++)
            {
                double sceneStart = usableStart + (i * sceneDuration);
                double sceneEnd = (i == snippetCount - 1) ? usableEnd : sceneStart + sceneDuration;
                activeScenes.Add((sceneStart, sceneEnd));
            }
        }

        var audioSegments = BuildAudioSegments(activeScenes, audioPoints, duration.Value, totalEffectiveDuration);
        var movementSegments = BuildMovementSegments(activeScenes, motionPoints, duration.Value, totalEffectiveDuration);
        var randomSegments = RegenerateRandomSegments(activeScenes, duration.Value, totalEffectiveDuration);

        var result = new VideoTimelineAnalysisResult
        {
            DurationSeconds = duration.Value,
            UsableStartSeconds = usableStart,
            UsableEndSeconds = usableEnd,
            Scenes = activeScenes,
            SceneCutTimestamps = sceneCutTimestamps,
            AudioModeSegments = audioSegments,
            MovementModeSegments = movementSegments,
            RandomModeSegments = randomSegments,
            MotionPoints = motionPoints,
            AudioPoints = audioPoints
        };

        if (videoEntry != null)
        {
            videoEntry.CachedAnalysis = result;

            lock (videoEntry.SavedConfigurations)
            {
                var soundOpts = new TrailerGenerationOptions
                {
                    Mode = TrailerGenerationMode.SoundDetect,
                    TargetDurationSeconds = options.TargetDurationSeconds,
                    SnippetCount = options.SnippetCount,
                    UseSceneChangeDetection = options.UseSceneChangeDetection,
                    UsePercentageOffset = options.UsePercentageOffset,
                    StartPercentage = options.StartPercentage,
                    FixedStartSeconds = options.FixedStartSeconds,
                    EndSkipPercentage = options.EndSkipPercentage
                };
                string soundKey = GetConfigurationKey(soundOpts);
                videoEntry.SavedConfigurations[soundKey] = result.AudioModeSegments.Select(s => s.Clone()).ToList();

                var moveOpts = new TrailerGenerationOptions
                {
                    Mode = TrailerGenerationMode.MovementDetection,
                    TargetDurationSeconds = options.TargetDurationSeconds,
                    SnippetCount = options.SnippetCount,
                    UseSceneChangeDetection = options.UseSceneChangeDetection,
                    UsePercentageOffset = options.UsePercentageOffset,
                    StartPercentage = options.StartPercentage,
                    FixedStartSeconds = options.FixedStartSeconds,
                    EndSkipPercentage = options.EndSkipPercentage
                };
                string moveKey = GetConfigurationKey(moveOpts);
                videoEntry.SavedConfigurations[moveKey] = result.MovementModeSegments.Select(s => s.Clone()).ToList();

                var randOpts = new TrailerGenerationOptions
                {
                    Mode = TrailerGenerationMode.Random,
                    TargetDurationSeconds = options.TargetDurationSeconds,
                    SnippetCount = options.SnippetCount,
                    UseSceneChangeDetection = options.UseSceneChangeDetection,
                    UsePercentageOffset = options.UsePercentageOffset,
                    StartPercentage = options.StartPercentage,
                    FixedStartSeconds = options.FixedStartSeconds,
                    EndSkipPercentage = options.EndSkipPercentage
                };
                string randKey = GetConfigurationKey(randOpts);
                videoEntry.SavedConfigurations[randKey] = result.RandomModeSegments.Select(s => s.Clone()).ToList();
            }

            logCallback?.Invoke($"Cached configurations for SoundDetect, MovementDetection, and Random modes on '{Path.GetFileName(videoPath)}'.");
        }

        return result;
    }

    /// <summary>
    /// Generates randomized segment start times across the provided scenes.
    /// Can be invoked instantaneously to re-roll random segment placements.
    /// </summary>
    public List<ManualSegmentItem> RegenerateRandomSegments(
        IReadOnlyList<(double SceneStart, double SceneEnd)> scenes,
        double totalVideoDuration,
        int targetDurationSeconds)
    {
        if (scenes == null || scenes.Count == 0)
        {
            return new List<ManualSegmentItem>();
        }

        var totalEffectiveDuration = Math.Max(5, targetDurationSeconds);
        double baseSegLength = (double)totalEffectiveDuration / scenes.Count;
        var segments = new List<ManualSegmentItem>(scenes.Count);

        foreach (var (sceneStart, sceneEnd) in scenes)
        {
            double currentSceneDuration = Math.Max(0.5, sceneEnd - sceneStart);
            double segLength = Math.Min(baseSegLength, currentSceneDuration);
            if (segLength < 0.5)
            {
                segLength = currentSceneDuration;
            }

            double maxStartInScene = Math.Max(sceneStart, sceneEnd - segLength);
            double jitterRange = Math.Max(0.0, maxStartInScene - sceneStart);
            double start = sceneStart + (Random.Shared.NextDouble() * jitterRange);
            var clampedStart = Math.Clamp(start, 0.0, Math.Max(0.0, totalVideoDuration - segLength));
            segments.Add(new ManualSegmentItem
            {
                StartSeconds = clampedStart,
                DurationSeconds = segLength
            });
        }

        return segments;
    }

    /// <summary>
    /// Extracts a single video frame at the specified timestamp as image bytes (BMP), piped directly from FFmpeg standard output.
    /// </summary>
    public async Task<byte[]?> ExtractFrameBytesAsync(
        string ffmpegPath,
        string videoPath,
        double timestampSeconds,
        int maxWidth,
        CancellationToken cancellationToken)
    {
        var binary = string.IsNullOrWhiteSpace(ffmpegPath) ? FindDefaultFfmpegPath() : ffmpegPath;
        if (!File.Exists(videoPath))
        {
            return null;
        }

        var ts = Math.Max(0.0, timestampSeconds).ToString("F3", CultureInfo.InvariantCulture);
        var scaleFilter = maxWidth > 0 ? $"-vf \"scale={maxWidth}:-2\" " : string.Empty;
        var args = $"-hide_banner -loglevel error -ss {ts} -i \"{videoPath}\" -frames:v 1 {scaleFilter}-f image2pipe -vcodec bmp -";

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch
        {
            return null;
        }

        using var ms = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await Task.WhenAll(copyTask, stdErrTask, process.WaitForExitAsync(cancellationToken));
            if (process.ExitCode == 0 && ms.Length > 0)
            {
                return ms.ToArray();
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill exceptions
            }

            throw;
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill exceptions
            }

            return null;
        }
    }

    /// <summary>
    /// Alias for ExtractFrameBytesAsync.
    /// </summary>
    public Task<byte[]?> ExtractFramePngOrJpegBytesAsync(
        string ffmpegPath,
        string videoPath,
        double timestampSeconds,
        int maxWidth,
        CancellationToken cancellationToken)
        => ExtractFrameBytesAsync(ffmpegPath, videoPath, timestampSeconds, maxWidth, cancellationToken);

    /// <summary>
    /// Divides usable video duration into scenes and computes snippet start times according to the chosen mode:
    /// Mode 0 (Random), Mode 1 (Sound Detect), or Mode 2 (Movement Detection).
    /// Supports re-using cached analysis points and scenes if provided.
    /// </summary>
    private async Task<List<(double StartSeconds, double DurationSeconds)>> BuildSegmentsForModeAsync(
        string ffmpegPath,
        string videoPath,
        double duration,
        TrailerGenerationOptions options,
        Action<string>? logCallback,
        CancellationToken cancellationToken,
        VideoTimelineAnalysisResult? cachedAnalysis = null)
    {
        var totalEffectiveDuration = Math.Max(5, options.TargetDurationSeconds);
        var snippetCount = Math.Clamp(options.SnippetCount, 1, 30);

        // 3. Start & End Skip Calculation (Applied to ALL 3 Modes)
        double startSkip;
        if (options.UsePercentageOffset)
        {
            double pct = Math.Clamp(options.StartPercentage, 0.0, 45.0) / 100.0;
            startSkip = duration * pct;
        }
        else
        {
            startSkip = Math.Clamp(options.FixedStartSeconds, 0.0, duration * 0.45);
        }

        double endPct = Math.Clamp(options.EndSkipPercentage, 0.0, 45.0) / 100.0;
        double endSkip = duration * endPct;

        double usableStart = Math.Clamp(startSkip, 0.0, duration * 0.85);
        double usableEnd = Math.Clamp(duration - endSkip, usableStart + 5.0, duration);

        double usableDuration = usableEnd - usableStart;
        if (usableDuration < totalEffectiveDuration + 2.0)
        {
            usableStart = 0.0;
            usableEnd = duration;
            usableDuration = usableEnd - usableStart;
        }

        List<(double SceneStart, double SceneEnd)> scenes;
        List<(double AbsoluteTimestamp, double SceneScore)>? cachedMotionPoints = cachedAnalysis?.MotionPoints;

        if (options.UseSceneChangeDetection)
        {
            if (cachedAnalysis != null && cachedAnalysis.Scenes is { Count: > 0 })
            {
                logCallback?.Invoke("Re-using cached scene detection for usable video span...");
                scenes = cachedAnalysis.Scenes;
            }
            else if (cachedMotionPoints is { Count: > 0 })
            {
                logCallback?.Invoke("Re-using cached motion points to build scene changes...");
                scenes = BuildScenesFromSceneChanges(
                    cachedMotionPoints,
                    usableStart,
                    usableEnd,
                    totalEffectiveDuration,
                    snippetCount);
            }
            else
            {
                logCallback?.Invoke("Detecting scene changes across usable video span...");
                cachedMotionPoints = await AnalyzeMovementAcrossUsableSpanAsync(
                    ffmpegPath,
                    videoPath,
                    usableStart,
                    usableDuration,
                    cancellationToken);

                scenes = BuildScenesFromSceneChanges(
                    cachedMotionPoints,
                    usableStart,
                    usableEnd,
                    totalEffectiveDuration,
                    snippetCount);
            }
        }
        else
        {
            scenes = new List<(double SceneStart, double SceneEnd)>(snippetCount);
            double sceneDuration = usableDuration / snippetCount;
            for (var i = 0; i < snippetCount; i++)
            {
                double sceneStart = usableStart + (i * sceneDuration);
                double sceneEnd = (i == snippetCount - 1) ? usableEnd : sceneStart + sceneDuration;
                scenes.Add((sceneStart, sceneEnd));
            }
        }

        double baseSegLength = (double)totalEffectiveDuration / scenes.Count;
        var segments = new List<(double StartSeconds, double DurationSeconds)>(scenes.Count);

        switch (options.Mode)
        {
            case TrailerGenerationMode.Random:
            {
                logCallback?.Invoke("Splitting into scenes & selecting random moments...");
                var items = RegenerateRandomSegments(scenes, duration, totalEffectiveDuration);
                segments.AddRange(items.Select(s => (s.StartSeconds, s.DurationSeconds)));
                break;
            }

            case TrailerGenerationMode.SoundDetect:
            {
                List<(double AbsoluteTimestamp, double RmsDb)> audioPoints;
                if (cachedAnalysis?.AudioPoints is { Count: > 0 })
                {
                    logCallback?.Invoke("Re-using cached audio analysis for loudest moments...");
                    audioPoints = cachedAnalysis.AudioPoints;
                }
                else
                {
                    logCallback?.Invoke("Analyzing scenes for loudest audio moments...");
                    audioPoints = await AnalyzeAudioRmsAcrossUsableSpanAsync(
                        ffmpegPath,
                        videoPath,
                        usableStart,
                        usableDuration,
                        cancellationToken);
                }

                var items = BuildAudioSegments(scenes, audioPoints, duration, totalEffectiveDuration);
                segments.AddRange(items.Select(s => (s.StartSeconds, s.DurationSeconds)));
                break;
            }

            case TrailerGenerationMode.MovementDetection:
            {
                List<(double AbsoluteTimestamp, double SceneScore)> motionPoints;
                if (cachedMotionPoints is { Count: > 0 })
                {
                    logCallback?.Invoke("Re-using cached motion analysis for highest movement moments...");
                    motionPoints = cachedMotionPoints;
                }
                else
                {
                    logCallback?.Invoke("Analyzing scenes for highest movement moments...");
                    motionPoints = await AnalyzeMovementAcrossUsableSpanAsync(
                        ffmpegPath,
                        videoPath,
                        usableStart,
                        usableDuration,
                        cancellationToken);
                }

                var items = BuildMovementSegments(scenes, motionPoints, duration, totalEffectiveDuration);
                segments.AddRange(items.Select(s => (s.StartSeconds, s.DurationSeconds)));
                break;
            }
        }

        logCallback?.Invoke($"Planned {segments.Count} segments of {baseSegLength:F1}s across usable span [{usableStart:F1}s - {usableEnd:F1}s].");
        return segments;
    }

    internal static List<ManualSegmentItem> BuildAudioSegments(
        IReadOnlyList<(double SceneStart, double SceneEnd)> scenes,
        IReadOnlyList<(double AbsoluteTimestamp, double RmsDb)> audioPoints,
        double totalVideoDuration,
        int totalEffectiveDuration)
    {
        if (scenes == null || scenes.Count == 0)
        {
            return new List<ManualSegmentItem>();
        }

        double baseSegLength = (double)totalEffectiveDuration / scenes.Count;
        var segments = new List<ManualSegmentItem>(scenes.Count);

        foreach (var (sceneStart, sceneEnd) in scenes)
        {
            double currentSceneDuration = Math.Max(0.5, sceneEnd - sceneStart);
            double segLength = Math.Min(baseSegLength, currentSceneDuration);
            if (segLength < 0.5)
            {
                segLength = currentSceneDuration;
            }

            double maxStartInScene = Math.Max(sceneStart, sceneEnd - segLength);
            double margin = Math.Min(0.5, currentSceneDuration * 0.1);
            var scenePoints = audioPoints
                .Where(p => p.AbsoluteTimestamp > sceneStart + margin && p.AbsoluteTimestamp < sceneEnd - margin && p.RmsDb > -900.0)
                .ToList();

            if (scenePoints.Count == 0)
            {
                scenePoints = audioPoints
                    .Where(p => p.AbsoluteTimestamp > sceneStart && p.AbsoluteTimestamp < sceneEnd && p.RmsDb > -900.0)
                    .ToList();
            }

            double start;
            if (scenePoints.Count > 0)
            {
                var loudPoint = scenePoints.MaxBy(p => p.RmsDb);
                double loudTimestamp = loudPoint.AbsoluteTimestamp;
                double idealStart = loudTimestamp - (segLength / 2.0);
                start = Math.Clamp(idealStart, sceneStart, maxStartInScene);
            }
            else
            {
                double midScene = (sceneStart + sceneEnd) / 2.0;
                start = Math.Clamp(midScene - (segLength / 2.0), sceneStart, maxStartInScene);
            }

            var clampedStart = Math.Clamp(start, 0.0, Math.Max(0.0, totalVideoDuration - segLength));
            segments.Add(new ManualSegmentItem
            {
                StartSeconds = clampedStart,
                DurationSeconds = segLength
            });
        }

        return segments;
    }

    internal static List<ManualSegmentItem> BuildMovementSegments(
        IReadOnlyList<(double SceneStart, double SceneEnd)> scenes,
        IReadOnlyList<(double AbsoluteTimestamp, double SceneScore)> motionPoints,
        double totalVideoDuration,
        int totalEffectiveDuration)
    {
        if (scenes == null || scenes.Count == 0)
        {
            return new List<ManualSegmentItem>();
        }

        double baseSegLength = (double)totalEffectiveDuration / scenes.Count;
        var segments = new List<ManualSegmentItem>(scenes.Count);

        foreach (var (sceneStart, sceneEnd) in scenes)
        {
            double currentSceneDuration = Math.Max(0.5, sceneEnd - sceneStart);
            double segLength = Math.Min(baseSegLength, currentSceneDuration);
            if (segLength < 0.5)
            {
                segLength = currentSceneDuration;
            }

            double maxStartInScene = Math.Max(sceneStart, sceneEnd - segLength);
            double margin = Math.Min(0.5, currentSceneDuration * 0.1);
            var scenePoints = motionPoints
                .Where(p => p.AbsoluteTimestamp > sceneStart + margin && p.AbsoluteTimestamp < sceneEnd - margin && p.SceneScore > 0.0)
                .ToList();

            if (scenePoints.Count == 0)
            {
                scenePoints = motionPoints
                    .Where(p => p.AbsoluteTimestamp > sceneStart && p.AbsoluteTimestamp < sceneEnd && p.SceneScore > 0.0)
                    .ToList();
            }

            double start;
            if (scenePoints.Count > 0)
            {
                var movePoint = scenePoints.MaxBy(p => p.SceneScore);
                double movementTimestamp = movePoint.AbsoluteTimestamp;
                double idealStart = movementTimestamp - (segLength / 2.0);
                start = Math.Clamp(idealStart, sceneStart, maxStartInScene);
            }
            else
            {
                double midScene = (sceneStart + sceneEnd) / 2.0;
                start = Math.Clamp(midScene - (segLength / 2.0), sceneStart, maxStartInScene);
            }

            var clampedStart = Math.Clamp(start, 0.0, Math.Max(0.0, totalVideoDuration - segLength));
            segments.Add(new ManualSegmentItem
            {
                StartSeconds = clampedStart,
                DurationSeconds = segLength
            });
        }

        return segments;
    }

    /// <summary>
    /// Builds contiguous scenes using FFmpeg scene change detection scores.
    /// Selects up to maxCuts timestamps greedy by scene score with minimum scene separation.
    /// </summary>
    private static List<(double SceneStart, double SceneEnd)> BuildScenesFromSceneChanges(
        List<(double AbsoluteTimestamp, double SceneScore)> motionPoints,
        double usableStart,
        double usableEnd,
        int totalEffectiveDuration,
        int fallbackSnippetCount)
    {
        int maxScenes = Math.Clamp(totalEffectiveDuration / 4, 2, 24);
        int maxCuts = maxScenes - 1;
        double minSceneLength = Math.Max(3.0, (usableEnd - usableStart) / (maxScenes * 2.0));

        var candidates = motionPoints
            .Where(p => p.AbsoluteTimestamp >= usableStart + minSceneLength &&
                        p.AbsoluteTimestamp <= usableEnd - minSceneLength &&
                        p.SceneScore >= 0.25)
            .OrderByDescending(p => p.SceneScore)
            .ToList();

        if (candidates.Count < 2)
        {
            candidates = motionPoints
                .Where(p => p.AbsoluteTimestamp >= usableStart + minSceneLength &&
                            p.AbsoluteTimestamp <= usableEnd - minSceneLength &&
                            p.SceneScore > 0.05)
                .OrderByDescending(p => p.SceneScore)
                .ToList();
        }

        var selectedCuts = new List<double>();
        foreach (var candidate in candidates)
        {
            if (selectedCuts.Count >= maxCuts)
            {
                break;
            }

            bool tooClose = false;
            for (int i = 0; i < selectedCuts.Count; i++)
            {
                if (Math.Abs(candidate.AbsoluteTimestamp - selectedCuts[i]) < minSceneLength)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
            {
                selectedCuts.Add(candidate.AbsoluteTimestamp);
            }
        }

        var scenes = new List<(double SceneStart, double SceneEnd)>();

        if (selectedCuts.Count > 0)
        {
            selectedCuts.Sort();
            double prev = usableStart;
            foreach (var cut in selectedCuts)
            {
                scenes.Add((prev, cut));
                prev = cut;
            }
            scenes.Add((prev, usableEnd));
            return scenes;
        }

        // If 0 cuts were found (e.g. static video), fall back to fallbackSnippetCount equal-length segments across [usableStart, usableEnd].
        int count = Math.Clamp(fallbackSnippetCount, 1, 30);
        double fallbackDuration = (usableEnd - usableStart) / count;
        for (int i = 0; i < count; i++)
        {
            double sceneStart = usableStart + (i * fallbackDuration);
            double sceneEnd = (i == count - 1) ? usableEnd : sceneStart + fallbackDuration;
            scenes.Add((sceneStart, sceneEnd));
        }

        return scenes;
    }

    /// <summary>
    /// Analyzes audio across [usableStart, usableStart + usableDuration] with FFmpeg astats and ametadata.
    /// Returns absolute movie timestamps and RMS decibel levels.
    /// </summary>
    private static async Task<List<(double AbsoluteTimestamp, double RmsDb)>> AnalyzeAudioRmsAcrossUsableSpanAsync(
        string ffmpegPath,
        string videoPath,
        double usableStart,
        double usableDuration,
        CancellationToken cancellationToken)
    {
        var points = new List<(double AbsoluteTimestamp, double RmsDb)>();
        var args =
            $"-hide_banner -loglevel info -ss {usableStart.ToString("F2", CultureInfo.InvariantCulture)} " +
            $"-t {usableDuration.ToString("F2", CultureInfo.InvariantCulture)} " +
            $"-i \"{videoPath}\" -vn " +
            $"-af \"astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-\" " +
            $"-f null -";

        var (_, stdout, stderr) = await RunProcessAsync(ffmpegPath, args, cancellationToken);
        var combined = stdout + "\n" + stderr;

        double currentPts = -1;
        using var reader = new StringReader(combined);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var ptsMatch = Regex.Match(line, @"pts_time:([0-9.]+)");
            if (ptsMatch.Success && double.TryParse(ptsMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
            {
                currentPts = pts;
                continue;
            }

            if (currentPts >= 0)
            {
                var rmsMatch = Regex.Match(line, @"lavfi\.astats\.Overall\.RMS_level=([-+]?[0-9.]+|[-+]?inf)", RegexOptions.IgnoreCase);
                if (rmsMatch.Success)
                {
                    var valStr = rmsMatch.Groups[1].Value;
                    double rms;
                    if (valStr.Contains("inf", StringComparison.OrdinalIgnoreCase) || valStr.Contains("nan", StringComparison.OrdinalIgnoreCase))
                    {
                        rms = -999.0;
                    }
                    else if (!double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rms))
                    {
                        rms = -999.0;
                    }

                    points.Add((usableStart + currentPts, rms));
                    currentPts = -1;
                }
            }
        }

        return points;
    }

    /// <summary>
    /// Analyzes visual movement across [usableStart, usableStart + usableDuration] with FFmpeg scene score filter.
    /// Returns absolute movie timestamps and visual scene change scores.
    /// </summary>
    private static async Task<List<(double AbsoluteTimestamp, double SceneScore)>> AnalyzeMovementAcrossUsableSpanAsync(
        string ffmpegPath,
        string videoPath,
        double usableStart,
        double usableDuration,
        CancellationToken cancellationToken)
    {
        var points = new List<(double AbsoluteTimestamp, double SceneScore)>();
        string skipFrameArg = usableDuration > 180.0 ? "-skip_frame nokey " : string.Empty;
        string vfFilter = usableDuration > 180.0
            ? "scale=160:-2,select='gte(scene,0)',metadata=print:file=-"
            : "fps=4,scale=160:-2,select='gte(scene,0)',metadata=print:file=-";

        var args =
            $"-hide_banner -loglevel info -ss {usableStart.ToString("F2", CultureInfo.InvariantCulture)} " +
            $"-t {usableDuration.ToString("F2", CultureInfo.InvariantCulture)} " +
            $"{skipFrameArg}-i \"{videoPath}\" " +
            $"-vf \"{vfFilter}\" " +
            $"-an -f null -";

        var (_, stdout, stderr) = await RunProcessAsync(ffmpegPath, args, cancellationToken);
        var combined = stdout + "\n" + stderr;

        double currentPts = -1;
        using var reader = new StringReader(combined);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var ptsMatch = Regex.Match(line, @"pts_time:([0-9.]+)");
            if (ptsMatch.Success && double.TryParse(ptsMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
            {
                currentPts = pts;
                continue;
            }

            if (currentPts >= 0)
            {
                var scoreMatch = Regex.Match(line, @"lavfi\.scene_score=([0-9.]+)");
                if (scoreMatch.Success && double.TryParse(scoreMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                {
                    points.Add((usableStart + currentPts, score));
                    currentPts = -1;
                }
            }
        }

        return points;
    }

    /// <summary>
    /// Extracts individual video segments to a thread-isolated temp directory and concatenates them into the final output.
    /// </summary>
    private static async Task ExtractAndConcatSegmentsAsync(
        string ffmpegPath,
        string videoPath,
        string outputTrailerPath,
        List<(double StartSeconds, double DurationSeconds)> segments,
        TrailerGenerationOptions options,
        Action<string>? logCallback,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jf_trailer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var segmentPaths = new List<string>();

            string scaleFilter;
            if (!string.IsNullOrWhiteSpace(options.ResolutionScale) &&
                !options.ResolutionScale.Equals("Original", StringComparison.OrdinalIgnoreCase) &&
                !options.ResolutionScale.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                scaleFilter = $"scale={options.ResolutionScale},";
            }
            else if (options.MaxHeightResolution > 0)
            {
                scaleFilter = $"scale=-2:'min({options.MaxHeightResolution},ih)',";
            }
            else
            {
                scaleFilter = string.Empty;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (startSec, durSec) = segments[i];
                var segPath = Path.Combine(tempDir, $"seg_{i:D3}.ts");

                var fadeDur = Math.Min(0.28, durSec * 0.18);
                var fadeOutStart = Math.Max(0.1, durSec - fadeDur);
                var fadeDurStr = fadeDur.ToString("F2", CultureInfo.InvariantCulture);
                var fadeOutStartStr = fadeOutStart.ToString("F2", CultureInfo.InvariantCulture);

                var videoFadeFilter = options.SmoothFadeTransitions
                    ? $",fade=t=in:st=0:d={fadeDurStr},fade=t=out:st={fadeOutStartStr}:d={fadeDurStr}"
                    : string.Empty;

                var vf = $"{scaleFilter}fps=30,format=yuv420p{videoFadeFilter}";

                string audioArgs;
                if (options.IncludeAudio)
                {
                    var audioFilter = options.SmoothFadeTransitions
                        ? $"-af \"afade=t=in:st=0:d={fadeDurStr},afade=t=out:st={fadeOutStartStr}:d={fadeDurStr}\" "
                        : string.Empty;
                    audioArgs = $"{audioFilter}-c:a aac -b:a 128k -ac 2 -ar 48000";
                }
                else
                {
                    audioArgs = "-an";
                }

                var args =
                    $"-hide_banner -loglevel error -y -ss {startSec.ToString("F2", CultureInfo.InvariantCulture)} -i \"{videoPath}\" " +
                    $"-t {durSec.ToString("F2", CultureInfo.InvariantCulture)} -map 0:v:0 {(options.IncludeAudio ? "-map 0:a:0?" : string.Empty)} " +
                    $"-vf \"{vf}\" -c:v libx264 -preset veryfast -crf 23 {audioArgs} -f mpegts \"{segPath}\"";

                var (exitCode, _, stderr) = await RunProcessAsync(ffmpegPath, args, cancellationToken);
                if (exitCode == 0 && File.Exists(segPath))
                {
                    segmentPaths.Add(segPath);
                }
                else
                {
                    logCallback?.Invoke($"Warning: segment {i + 1}/{segments.Count} failed ({stderr.Trim()}), continuing...");
                }
            }

            if (segmentPaths.Count == 0)
            {
                throw new InvalidOperationException("Failed to extract any video segments.");
            }

            logCallback?.Invoke($"Stitching {segmentPaths.Count} segments {(options.SmoothFadeTransitions ? "with smooth video/audio fades " : string.Empty)}into '{Path.GetFileName(outputTrailerPath)}'...");
            var tempOutputFile = Path.Combine(tempDir, "trailer_output.mp4");
            var concatInput = "concat:" + string.Join("|", segmentPaths);
            var concatArgs = $"-hide_banner -loglevel error -y -i \"{concatInput}\" -c copy -movflags +faststart \"{tempOutputFile}\"";

            var (concatExit, _, concatErr) = await RunProcessAsync(ffmpegPath, concatArgs, cancellationToken);
            if (concatExit != 0 || !File.Exists(tempOutputFile))
            {
                throw new InvalidOperationException($"Failed concatenating segments: {concatErr.Trim()}");
            }

            var targetDir = Path.GetDirectoryName(outputTrailerPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            if (File.Exists(outputTrailerPath))
            {
                File.Delete(outputTrailerPath);
            }

            try
            {
                File.Move(tempOutputFile, outputTrailerPath, overwrite: true);
            }
            catch (IOException)
            {
                File.Copy(tempOutputFile, outputTrailerPath, overwrite: true);
                File.Delete(tempOutputFile);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup failure
            }
        }
    }

    private static string GetSiblingFfprobePath(string ffmpegPath)
    {
        var dir = Path.GetDirectoryName(ffmpegPath);
        return string.IsNullOrEmpty(dir)
            ? "ffprobe.exe"
            : Path.Combine(dir, "ffprobe.exe");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;
            return (process.ExitCode, stdOut, stdErr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill exceptions
            }

            throw;
        }
    }

    /// <summary>
    /// Streams a smooth continuous 24 FPS video preview with native synchronized PCM audio playback
    /// from the specified timestamp for up to maxDurationSeconds.
    /// </summary>
    public async Task StreamSmoothPreviewAsync(
        string ffmpegPath,
        string videoPath,
        double startTimestampSeconds,
        double maxDurationSeconds,
        int maxWidth,
        Func<bool> isMutedProvider,
        Action<byte[], double> onFrameDecoded,
        CancellationToken cancellationToken,
        Func<float>? volumeProvider = null)
    {
        var binary = string.IsNullOrWhiteSpace(ffmpegPath) ? FindDefaultFfmpegPath() : ffmpegPath;
        if (!File.Exists(videoPath))
        {
            return;
        }

        string ts = Math.Max(0.0, startTimestampSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        string remDur = Math.Max(0.1, maxDurationSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        int safeWidth = Math.Max(160, (maxWidth / 2) * 2);

        Process? videoProcess = null;
        Process? audioProcess = null;
        Task? audioTask = null;

        try
        {
            // 1. Audio Process (s16le 44.1kHz stereo pipe)
            var audioPsi = new ProcessStartInfo
            {
                FileName = binary,
                Arguments = $"-hide_banner -loglevel error -ss {ts} -t {remDur} -i \"{videoPath}\" -vn -ac 2 -ar 44100 -acodec pcm_s16le -f s16le -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                audioProcess = new Process { StartInfo = audioPsi };
                audioProcess.Start();

                audioTask = Task.Run(async () =>
                {
                    try
                    {
                        using var audioPlayer = new WinmmAudioPlayer(isMutedProvider, volumeProvider);
                        await audioPlayer.PlayPcmStreamAsync(audioProcess.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation requested
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Audio playback ignored error: {ex.Message}");
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not start preview audio process: {ex.Message}");
            }

            // 2. Video Process (Continuous 24 FPS BMP Pipe)
            var videoPsi = new ProcessStartInfo
            {
                FileName = binary,
                Arguments = $"-hide_banner -loglevel error -ss {ts} -t {remDur} -i \"{videoPath}\" -an -vf \"fps=24,scale={safeWidth}:-2\" -f image2pipe -vcodec bmp -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            videoProcess = new Process { StartInfo = videoPsi };
            videoProcess.Start();

            var videoStream = videoProcess.StandardOutput.BaseStream;
            byte[] header = new byte[14];
            Stopwatch? sw = null;
            int frameIndex = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                bool readHeader = await ReadExactAsync(videoStream, header, 0, 14, cancellationToken).ConfigureAwait(false);
                if (!readHeader)
                {
                    break;
                }

                if (header[0] != (byte)'B' || header[1] != (byte)'M')
                {
                    break;
                }

                int bmpFileSize = BitConverter.ToInt32(header, 2);
                if (bmpFileSize <= 54 || bmpFileSize >= 20_000_000)
                {
                    break;
                }

                byte[] bmpBytes = new byte[bmpFileSize];
                Buffer.BlockCopy(header, 0, bmpBytes, 0, 14);

                bool readBody = await ReadExactAsync(videoStream, bmpBytes, 14, bmpFileSize - 14, cancellationToken).ConfigureAwait(false);
                if (!readBody)
                {
                    break;
                }

                sw ??= Stopwatch.StartNew();

                double targetSec = frameIndex / 24.0;
                double actualSec = sw.Elapsed.TotalSeconds;

                if (targetSec > actualSec + 0.005)
                {
                    var delay = TimeSpan.FromSeconds(targetSec - actualSec);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                actualSec = sw.Elapsed.TotalSeconds;

                if (actualSec > targetSec + 0.15)
                {
                    // UI/CPU is slightly behind real-time; skip this frame so video does not lag
                }
                else
                {
                    double currentTimestamp = startTimestampSeconds + sw.Elapsed.TotalSeconds;
                    onFrameDecoded(bmpBytes, currentTimestamp);
                }

                frameIndex++;
            }
        }
        finally
        {
            KillProcessSafely(videoProcess);
            KillProcessSafely(audioProcess);

            if (audioTask != null)
            {
                try
                {
                    await audioTask.ConfigureAwait(false);
                }
                catch
                {
                    // Ignore background task exceptions on shutdown
                }
            }

            videoProcess?.Dispose();
            audioProcess?.Dispose();
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }
            totalRead += read;
        }
        return true;
    }

    private static void KillProcessSafely(Process? process)
    {
        if (process == null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore kill exceptions
        }
    }
}
