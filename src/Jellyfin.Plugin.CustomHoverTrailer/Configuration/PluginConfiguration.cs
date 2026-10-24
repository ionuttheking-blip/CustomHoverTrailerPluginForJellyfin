using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CustomHoverTrailer.Configuration;

/// <summary>
/// Per-library trailer source priority rule.
/// </summary>
public class LibraryPriorityOverride
{
    /// <summary>
    /// Gets or sets the Jellyfin virtual folder / library ItemId (or name).
    /// </summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name of the Jellyfin library.
    /// </summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this library uses a custom source priority order instead of the global default.
    /// </summary>
    public bool UseCustomPriority { get; set; }

    /// <summary>
    /// Gets or sets the comma-separated ordered list of trailer sources for this library.
    /// Valid tokens: "LocalDisk", "RemoteMp4", "YouTube".
    /// </summary>
    public string SourcePriorityOrder { get; set; } = "LocalDisk,RemoteMp4,YouTube";
}

/// <summary>
/// Configuration settings for the Custom Hover Trailer plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether hover previews are enabled.
    /// </summary>
    public bool EnableHoverPreviews { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether TV / Gamepad / Keyboard D-pad focus previews are enabled.
    /// When enabled, selecting a card with a TV remote or arrow keys plays the trailer inside the focused card.
    /// </summary>
    public bool EnableTvFocusPreviews { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether mobile/touchscreen viewport center auto-previews are enabled.
    /// When enabled on phones/tablets, the card closest to the center of the screen plays automatically after scrolling stops.
    /// </summary>
    public bool EnableMobileCenterViewportPreviews { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether swiping left-to-right on a movie poster card starts/switches trailer playback.
    /// </summary>
    public bool EnableMobileSwipeToPlay { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether mobile devices should ONLY play trailers when swiped left-to-right
    /// (disables automatic center-of-screen playback on phones/tablets).
    /// </summary>
    public bool MobileSwipeOnlyMode { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether all available trailers for a movie should cycle sequentially
    /// in the configured priority order instead of looping only the first working trailer.
    /// </summary>
    public bool CycleAllAvailableTrailers { get; set; } = true;

    /// <summary>
    /// Gets or sets the hover delay in milliseconds before playback begins.
    /// </summary>
    public int HoverDelayMs { get; set; } = 400;

    /// <summary>
    /// Gets or sets the maximum playback duration on hover in seconds (default 30s).
    /// </summary>
    public int MaxPreviewDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets a value indicating whether hover playback starts muted.
    /// </summary>
    public bool StartMuted { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to show a small mute/unmute button in the card corner.
    /// </summary>
    public bool ShowMuteButton { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to smoothly widen the hovered card to 16:9 in-place (Option B sub-mode).
    /// When false, the 16:9 video is letterboxed (object-fit: contain) inside the existing poster card dimensions.
    /// </summary>
    public bool ExpandCardToWidescreenInPlace { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to enable local *name_of_video*-trailer.mp4 detection directly from disk.
    /// </summary>
    public bool EnableLocalDiskTrailers { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to enable direct remote .mp4/.webm URLs from item metadata.
    /// </summary>
    public bool EnableRemoteMp4Urls { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to resolve YouTube trailer URLs via YoutubeExplode into a native HTML5 video stream.
    /// </summary>
    public bool EnableYouTubeFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets the global default priority order of trailer sources (comma-separated).
    /// Valid tokens: "LocalDisk", "RemoteMp4", "YouTube".
    /// </summary>
    public string DefaultSourcePriorityOrder { get; set; } = "LocalDisk,RemoteMp4,YouTube";

    /// <summary>
    /// Gets or sets per-library priority overrides.
    /// </summary>
    public List<LibraryPriorityOverride> LibraryPriorityOverrides { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether verbose diagnostic logs should be printed in the browser console.
    /// </summary>
    public bool EnableBrowserConsoleLogging { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to automatically inject the client script into Jellyfin Web's index.html on server start.
    /// </summary>
    public bool AutoInjectClientScript { get; set; } = true;
}
