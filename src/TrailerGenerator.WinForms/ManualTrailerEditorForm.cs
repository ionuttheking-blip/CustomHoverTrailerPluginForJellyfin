using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using TrailerGenerator.WinForms.Models;
using TrailerGenerator.WinForms.Services;

namespace TrailerGenerator.WinForms;

/// <summary>
/// Interactive manual trailer editor window providing a video viewport, scrubbing controls,
/// smooth 24 FPS preview with synchronized audio playback, volume slider, side-by-side
/// existing trailer comparison, stacked timeline visualization (Detected Scenes, Audio,
/// Movement, Random, and Manual Segments), instant opening via cached analysis, re-analysis controls,
/// and saved configuration preset selection.
/// </summary>
public sealed class ManualTrailerEditorForm : Form
{
    private const int TimelineMargin = 12;

    private readonly VideoEntry _videoEntry;
    private readonly FfmpegGeneratorService _generatorService;
    private readonly TrailerGenerationOptions _baseOptions;
    private readonly int _initialVolume;

    private readonly CancellationTokenSource _formCts = new();
    private CancellationTokenSource? _scrubCts;
    private CancellationTokenSource? _playbackCts;
    private int _uiFrameUpdateInFlight;
    private volatile bool _isMuted;

    private VideoTimelineAnalysisResult? _analysis;
    private List<ManualSegmentItem> _workingSegments = new();
    private double _currentSeconds;
    private double _durationSeconds;
    private bool _isPlaying;
    private bool _isUpdatingUi;
    private int _selectedSegmentIndex = -1;

    // Timeline dragging state
    private bool _isDraggingSegment;
    private int _draggedSegmentIndex = -1;
    private double _dragOffsetSeconds;

    // Top status strip
    private readonly Label _videoTitleLabel = new();
    private readonly Label _statusLabel = new();

    // Video viewport & side-by-side trailer comparison
    private readonly TableLayoutPanel _previewSplitContainer = new();
    private readonly PictureBox _videoPreviewBox = new();
    private readonly Panel _trailerPreviewPanel = new();
    private readonly PictureBox _existingTrailerPreviewBox = new();
    private readonly Button _btnPlayPauseTrailer = new();
    private readonly CheckBox _chkMuteTrailer = new() { Text = "Mute Trailer", Checked = true, AutoSize = true, ForeColor = Color.LightGray };
    private readonly TrackBar _trailerScrubber = new();
    private readonly Label _lblTrailerTime = new();
    private double _trailerDurationSeconds;
    private CancellationTokenSource? _trailerScrubCts;
    private CancellationTokenSource? _trailerPlaybackCts;
    private bool _isTrailerPlaying;
    private int _trailerFrameUpdateInFlight;

    // Transport controls
    private readonly Button _btnStepBack10 = new();
    private readonly Button _btnStepBack1 = new();
    private readonly Button _btnPlayPause = new();
    private readonly Button _btnStepForward1 = new();
    private readonly Button _btnStepForward10 = new();
    private readonly Button _btnAddSegmentHere = new();
    private readonly CheckBox _muteCheckBox = new();
    private readonly TrackBar _volumeTrackBar = new();
    private readonly Label _lblVolume = new();
    private readonly CheckBox _chkCompareTrailer = new();
    private readonly Button _btnReanalyze = new();
    private readonly Label _timestampReadoutLabel = new();

    // Main video scrubber
    private readonly TrackBar _videoScrollBar = new();

    // Stacked timeline canvases
    private readonly DoubleBufferedPanel _scenesCanvas = new();
    private readonly DoubleBufferedPanel _audioCanvas = new();
    private readonly DoubleBufferedPanel _movementCanvas = new();
    private readonly DoubleBufferedPanel _randomCanvas = new();
    private readonly DoubleBufferedPanel _manualCanvas = new();

    // Timeline track action buttons
    private readonly Button _btnApplyAudio = new();
    private readonly Button _btnApplyMovement = new();
    private readonly Button _btnRerollRandom = new();
    private readonly Button _btnApplyRandom = new();

    // Bottom section: Grid and Fine-tuning controls
    private readonly DoubleBufferedDataGridView _segmentsGrid = new();
    private readonly NumericUpDown _segmentStartNumeric = new();
    private readonly NumericUpDown _segmentDurationNumeric = new();
    private readonly Button _setStartToCurrentButton = new();
    private readonly Button _addSegmentAtScrollButton = new();
    private readonly Button _removeSegmentButton = new();
    private readonly Label _summaryLabel = new();

    // Bottom dialog action buttons & saved configuration presets
    private readonly Button _btnResetToAuto = new();
    private readonly ComboBox _savedConfigsComboBox = new();
    private readonly Button _btnApplySavedConfig = new();
    private readonly Button _btnSaveManual = new();
    private readonly Button _btnCancel = new();
    private readonly ToolTip _actionToolTip = new();

    /// <summary>
    /// Gets whether the user successfully saved manual trailer segments.
    /// </summary>
    public bool SavedAsManual { get; private set; }

    /// <summary>
    /// Gets whether the user requested resetting this video to automatic mode.
    /// </summary>
    public bool ResetToAutomaticRequested { get; private set; }

    public ManualTrailerEditorForm(
        VideoEntry videoEntry,
        FfmpegGeneratorService generatorService,
        TrailerGenerationOptions baseOptions,
        int defaultPreviewVolume = 50)
    {
        _videoEntry = videoEntry ?? throw new ArgumentNullException(nameof(videoEntry));
        _generatorService = generatorService ?? throw new ArgumentNullException(nameof(generatorService));
        _baseOptions = baseOptions ?? throw new ArgumentNullException(nameof(baseOptions));
        _initialVolume = Math.Clamp(defaultPreviewVolume, 0, 100);

        Width = 1140;
        Height = 820;
        MinimumSize = new Size(960, 680);
        StartPosition = FormStartPosition.CenterParent;
        Text = $"Manual Trailer Editor - {_videoEntry.FileName}";
        Font = new Font("Segoe UI", 9.5F);

        InitializeComponentLayout();
        InitializeEvents();
    }

    private void InitializeComponentLayout()
    {
        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(12)
        };

        // Heights: Header (34px), Video Preview (50% responsive), Transport (38px), Timelines (200px), Grid & Controls (50%), Actions (50px)
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));  // 0: Header
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));   // 1: Video Preview
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));  // 2: Transport bar
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 200)); // 3: Stacked Timelines
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));   // 4: Segments Grid & Controls
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));  // 5: Action buttons

        // -------------------------------------------------------------
        // Row 0: Top Header Strip
        // -------------------------------------------------------------
        var headerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        _videoTitleLabel.Text = $"Source: {_videoEntry.FileName}";
        _videoTitleLabel.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        _videoTitleLabel.AutoEllipsis = true;
        _videoTitleLabel.Dock = DockStyle.Fill;
        _videoTitleLabel.TextAlign = ContentAlignment.MiddleLeft;

        _statusLabel.Text = "Initializing analysis...";
        _statusLabel.ForeColor = Color.FromArgb(0, 102, 204);
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleRight;

        headerPanel.Controls.Add(_videoTitleLabel, 0, 0);
        headerPanel.Controls.Add(_statusLabel, 1, 0);
        rootLayout.Controls.Add(headerPanel, 0, 0);

        // -------------------------------------------------------------
        // Row 1: Video Preview Viewport & Side-by-Side Comparison Container
        // -------------------------------------------------------------
        var previewContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(16, 16, 20),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 2, 0, 4)
        };

        _previewSplitContainer.Dock = DockStyle.Fill;
        _previewSplitContainer.ColumnCount = 2;
        _previewSplitContainer.RowCount = 1;
        _previewSplitContainer.Margin = new Padding(0);
        _previewSplitContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _previewSplitContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 0f));

        _videoPreviewBox.Dock = DockStyle.Fill;
        _videoPreviewBox.SizeMode = PictureBoxSizeMode.Zoom;
        _videoPreviewBox.BackColor = Color.FromArgb(16, 16, 20);

        // Construct existing trailer comparison panel
        var trailerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0)
        };
        trailerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // Header
        trailerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Preview
        trailerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); // Scrubber & Time

        var lblTopTrailer = new Label
        {
            Text = "Existing Trailer",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(30, 30, 38)
        };

        _existingTrailerPreviewBox.Dock = DockStyle.Fill;
        _existingTrailerPreviewBox.SizeMode = PictureBoxSizeMode.Zoom;
        _existingTrailerPreviewBox.BackColor = Color.FromArgb(16, 16, 20);

        var miniTransport = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = Color.FromArgb(24, 24, 30)
        };
        miniTransport.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        miniTransport.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        miniTransport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        miniTransport.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));

        _btnPlayPauseTrailer.Text = "▶ Play";
        _btnPlayPauseTrailer.Width = 65;
        _btnPlayPauseTrailer.Height = 26;
        _btnPlayPauseTrailer.Font = new Font("Segoe UI", 8F);
        _btnPlayPauseTrailer.Margin = new Padding(2, 2, 2, 2);
        _btnPlayPauseTrailer.BackColor = Color.FromArgb(45, 45, 55);
        _btnPlayPauseTrailer.ForeColor = Color.White;
        _btnPlayPauseTrailer.FlatStyle = FlatStyle.Flat;

        _chkMuteTrailer.Text = "Mute Trailer";
        _chkMuteTrailer.Checked = true;
        _chkMuteTrailer.AutoSize = true;
        _chkMuteTrailer.ForeColor = Color.LightGray;
        _chkMuteTrailer.Font = new Font("Segoe UI", 8F);
        _chkMuteTrailer.Margin = new Padding(4, 5, 4, 2);

        _trailerScrubber.Dock = DockStyle.Fill;
        _trailerScrubber.Minimum = 0;
        _trailerScrubber.Maximum = 600;
        _trailerScrubber.TickStyle = TickStyle.None;
        _trailerScrubber.Margin = new Padding(2, 2, 2, 2);

        _lblTrailerTime.Text = "00:00.0";
        _lblTrailerTime.ForeColor = Color.LightGray;
        _lblTrailerTime.Dock = DockStyle.Fill;
        _lblTrailerTime.TextAlign = ContentAlignment.MiddleCenter;
        _lblTrailerTime.Font = new Font("Segoe UI", 8F);

        miniTransport.Controls.Add(_btnPlayPauseTrailer, 0, 0);
        miniTransport.Controls.Add(_chkMuteTrailer, 1, 0);
        miniTransport.Controls.Add(_trailerScrubber, 2, 0);
        miniTransport.Controls.Add(_lblTrailerTime, 3, 0);

        trailerLayout.Controls.Add(lblTopTrailer, 0, 0);
        trailerLayout.Controls.Add(_existingTrailerPreviewBox, 0, 1);
        trailerLayout.Controls.Add(miniTransport, 0, 2);

        _trailerPreviewPanel.Dock = DockStyle.Fill;
        _trailerPreviewPanel.Margin = new Padding(2, 0, 0, 0);
        _trailerPreviewPanel.Controls.Add(trailerLayout);

        _previewSplitContainer.Controls.Add(_videoPreviewBox, 0, 0);
        _previewSplitContainer.Controls.Add(_trailerPreviewPanel, 1, 0);
        previewContainer.Controls.Add(_previewSplitContainer);

        rootLayout.Controls.Add(previewContainer, 0, 1);

        // -------------------------------------------------------------
        // Row 2: Transport & Step Controls Bar
        // -------------------------------------------------------------
        var transportPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Margin = new Padding(0, 2, 0, 2)
        };

        ConfigureButton(_btnStepBack10, "<< -10s", 65);
        ConfigureButton(_btnStepBack1, "< -1s", 55);
        ConfigureButton(_btnPlayPause, "▶ Play Preview", 115, isPrimary: true);
        _btnPlayPause.BackColor = Color.FromArgb(40, 120, 65);
        ConfigureButton(_btnStepForward1, "+1s >", 55);
        ConfigureButton(_btnStepForward10, "+10s >>", 65);
        ConfigureButton(_btnAddSegmentHere, "+ Add Segment Here", 145);

        _muteCheckBox.Text = "Mute Audio";
        _muteCheckBox.Checked = false;
        _muteCheckBox.AutoSize = true;
        _muteCheckBox.Font = new Font("Segoe UI", 9F);
        _muteCheckBox.Margin = new Padding(8, 6, 0, 0);

        _volumeTrackBar.Minimum = 0;
        _volumeTrackBar.Maximum = 100;
        _volumeTrackBar.Value = _initialVolume;
        _volumeTrackBar.TickStyle = TickStyle.None;
        _volumeTrackBar.Width = 95;
        _volumeTrackBar.Height = 26;
        _volumeTrackBar.Margin = new Padding(6, 5, 0, 0);

        _lblVolume.Text = $"Vol: {_initialVolume}%";
        _lblVolume.AutoSize = true;
        _lblVolume.Font = new Font("Segoe UI", 8.5F);
        _lblVolume.Margin = new Padding(2, 8, 6, 0);

        bool hasExistingTrailer = !string.IsNullOrWhiteSpace(_videoEntry.TrailerPath) && File.Exists(_videoEntry.TrailerPath);
        _chkCompareTrailer.AutoSize = true;
        _chkCompareTrailer.Font = new Font("Segoe UI", 9F);
        _chkCompareTrailer.Margin = new Padding(6, 6, 0, 0);
        if (!hasExistingTrailer)
        {
            _chkCompareTrailer.Enabled = false;
            _chkCompareTrailer.Text = "No Existing Trailer";
        }
        else
        {
            _chkCompareTrailer.Enabled = true;
            _chkCompareTrailer.Text = "🔀 Compare Existing Trailer";
        }

        _btnReanalyze.Text = "🔄 Re-analyze File";
        _btnReanalyze.AutoSize = true;
        _btnReanalyze.Height = 30;
        _btnReanalyze.BackColor = Color.FromArgb(235, 240, 248);
        _btnReanalyze.Font = new Font("Segoe UI", 9F);
        _btnReanalyze.Margin = new Padding(6, 2, 0, 2);

        _timestampReadoutLabel.Text = "00:00:00.0 / 00:00:00.0";
        _timestampReadoutLabel.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        _timestampReadoutLabel.ForeColor = Color.FromArgb(30, 30, 30);
        _timestampReadoutLabel.AutoSize = true;
        _timestampReadoutLabel.Margin = new Padding(10, 6, 0, 0);

        transportPanel.Controls.Add(_btnStepBack10);
        transportPanel.Controls.Add(_btnStepBack1);
        transportPanel.Controls.Add(_btnPlayPause);
        transportPanel.Controls.Add(_btnStepForward1);
        transportPanel.Controls.Add(_btnStepForward10);
        transportPanel.Controls.Add(_btnAddSegmentHere);
        transportPanel.Controls.Add(_muteCheckBox);
        transportPanel.Controls.Add(_volumeTrackBar);
        transportPanel.Controls.Add(_lblVolume);
        transportPanel.Controls.Add(_chkCompareTrailer);
        transportPanel.Controls.Add(_btnReanalyze);
        transportPanel.Controls.Add(_timestampReadoutLabel);

        rootLayout.Controls.Add(transportPanel, 0, 2);

        // -------------------------------------------------------------
        // Row 3: Stacked Visual Timeline Bars Directly Under Scroll Bar
        // -------------------------------------------------------------
        var timelineTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Margin = new Padding(0, 2, 0, 4)
        };
        timelineTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260)); // Track headers & buttons
        timelineTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // Canvas & TrackBar

        timelineTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // 0: Video Scrubber
        timelineTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); // 1: Detected Scenes
        timelineTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // 2: Audio Mode
        timelineTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // 3: Movement Mode
        timelineTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // 4: Random Mode
        timelineTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); // 5: Manual Segments

        // 0: Scrubber TrackBar
        var lblScrubber = CreateTrackLabel("Video Position:");
        _videoScrollBar.Dock = DockStyle.Fill;
        _videoScrollBar.Minimum = 0;
        _videoScrollBar.Maximum = 100;
        _videoScrollBar.TickStyle = TickStyle.None;
        _videoScrollBar.Margin = new Padding(0, 2, 0, 0);
        timelineTable.Controls.Add(lblScrubber, 0, 0);
        timelineTable.Controls.Add(_videoScrollBar, 1, 0);

        // 1: Detected Scenes Bar
        var lblScenes = CreateTrackLabel("1. Detected Scene Changes:");
        ConfigureTimelineCanvas(_scenesCanvas);
        timelineTable.Controls.Add(lblScenes, 0, 1);
        timelineTable.Controls.Add(_scenesCanvas, 1, 1);

        // 2: Audio Mode Bar
        var pnlAudioHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var lblAudio = CreateTrackLabel("2. Audio Mode:");
        lblAudio.AutoSize = false;
        lblAudio.Width = 115;
        ConfigureSmallActionButton(_btnApplyAudio, "Use Audio Mode", 125);
        pnlAudioHeader.Controls.Add(lblAudio);
        pnlAudioHeader.Controls.Add(_btnApplyAudio);

        ConfigureTimelineCanvas(_audioCanvas);
        timelineTable.Controls.Add(pnlAudioHeader, 0, 2);
        timelineTable.Controls.Add(_audioCanvas, 1, 2);

        // 3: Movement Mode Bar
        var pnlMoveHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var lblMovement = CreateTrackLabel("3. Movement Mode:");
        lblMovement.AutoSize = false;
        lblMovement.Width = 115;
        ConfigureSmallActionButton(_btnApplyMovement, "Use Movement Mode", 130);
        pnlMoveHeader.Controls.Add(lblMovement);
        pnlMoveHeader.Controls.Add(_btnApplyMovement);

        ConfigureTimelineCanvas(_movementCanvas);
        timelineTable.Controls.Add(pnlMoveHeader, 0, 3);
        timelineTable.Controls.Add(_movementCanvas, 1, 3);

        // 4: Random Mode Bar
        var pnlRandomHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        ConfigureSmallActionButton(_btnRerollRandom, "🎲 Regenerate Random", 135);
        ConfigureSmallActionButton(_btnApplyRandom, "Use Random Mode", 115);
        pnlRandomHeader.Controls.Add(_btnRerollRandom);
        pnlRandomHeader.Controls.Add(_btnApplyRandom);

        ConfigureTimelineCanvas(_randomCanvas);
        timelineTable.Controls.Add(pnlRandomHeader, 0, 4);
        timelineTable.Controls.Add(_randomCanvas, 1, 4);

        // 5: Manual Trailer Segments (Interactive / Draggable) Bar
        var lblManual = CreateTrackLabel("5. Manual Trailer Segments:");
        lblManual.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        ConfigureTimelineCanvas(_manualCanvas);
        timelineTable.Controls.Add(lblManual, 0, 5);
        timelineTable.Controls.Add(_manualCanvas, 1, 5);

        rootLayout.Controls.Add(timelineTable, 0, 3);

        // -------------------------------------------------------------
        // Row 4: Bottom Section: Segments Grid & Fine-Tuning Controls
        // -------------------------------------------------------------
        var bottomSplit = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 4, 0, 4)
        };
        bottomSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        bottomSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));

        // DataGridView
        ConfigureSegmentsGrid();
        bottomSplit.Controls.Add(_segmentsGrid, 0, 0);

        // Fine-tuning Panel
        var fineTuningGroup = new GroupBox
        {
            Text = "Manual Segment Fine-Tuning",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Font = new Font("Segoe UI", 9F)
        };

        var fineTuningLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5
        };
        fineTuningLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        fineTuningLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        fineTuningLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        fineTuningLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        fineTuningLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        fineTuningLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        fineTuningLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var lblStart = new Label { Text = "Selected Segment Start (s):", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        _segmentStartNumeric.Dock = DockStyle.Fill;
        _segmentStartNumeric.DecimalPlaces = 1;
        _segmentStartNumeric.Increment = 0.5m;
        _segmentStartNumeric.Minimum = 0;
        _segmentStartNumeric.Maximum = 100000;

        var lblDur = new Label { Text = "Duration (s):", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        _segmentDurationNumeric.Dock = DockStyle.Fill;
        _segmentDurationNumeric.DecimalPlaces = 1;
        _segmentDurationNumeric.Increment = 0.5m;
        _segmentDurationNumeric.Minimum = 0.5m;
        _segmentDurationNumeric.Maximum = 60m;
        _segmentDurationNumeric.Value = 5.0m;

        _setStartToCurrentButton.Text = "Set Start to Current Scroll Position";
        _setStartToCurrentButton.Dock = DockStyle.Fill;
        _setStartToCurrentButton.Font = new Font("Segoe UI", 8F);

        _addSegmentAtScrollButton.Text = "+ Add Segment at Scroll Position";
        _addSegmentAtScrollButton.Dock = DockStyle.Fill;
        _addSegmentAtScrollButton.Font = new Font("Segoe UI", 8F);

        _removeSegmentButton.Text = "Remove Segment";
        _removeSegmentButton.Dock = DockStyle.Fill;
        _removeSegmentButton.Font = new Font("Segoe UI", 8.5F);

        _summaryLabel.Text = "Segments: 0 | Total: 0.0s";
        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);

        fineTuningLayout.Controls.Add(lblStart, 0, 0);
        fineTuningLayout.Controls.Add(_segmentStartNumeric, 1, 0);
        fineTuningLayout.Controls.Add(lblDur, 0, 1);
        fineTuningLayout.Controls.Add(_segmentDurationNumeric, 1, 1);
        fineTuningLayout.Controls.Add(_setStartToCurrentButton, 0, 2);
        fineTuningLayout.Controls.Add(_addSegmentAtScrollButton, 1, 2);
        fineTuningLayout.Controls.Add(_removeSegmentButton, 0, 3);
        fineTuningLayout.Controls.Add(_summaryLabel, 0, 4);
        fineTuningLayout.SetColumnSpan(_summaryLabel, 2);

        fineTuningGroup.Controls.Add(fineTuningLayout);
        bottomSplit.Controls.Add(fineTuningGroup, 1, 0);

        rootLayout.Controls.Add(bottomSplit, 0, 4);

        // -------------------------------------------------------------
        // Row 5: Bottom Dialog Action Buttons & Saved Config Presets
        // -------------------------------------------------------------
        var actionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0)
        };
        actionPanel.ColumnStyles.Clear();
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); // Left: Reset to Auto
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // Middle: Saved Configs
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270)); // Right: Save / Cancel

        _btnResetToAuto.Text = "Reset to Auto Mode";
        _btnResetToAuto.Dock = DockStyle.Fill;
        _btnResetToAuto.Height = 36;
        _btnResetToAuto.Margin = new Padding(2, 2, 6, 2);
        _btnResetToAuto.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _btnResetToAuto.BackColor = Color.FromArgb(254, 240, 240);
        _btnResetToAuto.ForeColor = Color.FromArgb(170, 30, 30);
        _btnResetToAuto.FlatStyle = FlatStyle.Flat;
        _btnResetToAuto.FlatAppearance.BorderColor = Color.FromArgb(220, 130, 130);
        _btnResetToAuto.FlatAppearance.BorderSize = 1;

        var savedConfigsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(2, 2, 2, 2)
        };

        var lblSavedConfigs = new Label
        {
            Text = "Preset:",
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Margin = new Padding(4, 9, 2, 0)
        };

        _savedConfigsComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _savedConfigsComboBox.Width = 210;
        _savedConfigsComboBox.Height = 28;
        _savedConfigsComboBox.Font = new Font("Segoe UI", 8.5F);
        _savedConfigsComboBox.Margin = new Padding(2, 6, 4, 2);

        _btnApplySavedConfig.Text = "Apply Saved Config";
        _btnApplySavedConfig.AutoSize = true;
        _btnApplySavedConfig.Height = 32;
        _btnApplySavedConfig.Font = new Font("Segoe UI", 8.5F);
        _btnApplySavedConfig.Margin = new Padding(2, 4, 2, 2);

        savedConfigsPanel.Controls.Add(lblSavedConfigs);
        savedConfigsPanel.Controls.Add(_savedConfigsComboBox);
        savedConfigsPanel.Controls.Add(_btnApplySavedConfig);

        var rightActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };

        _btnSaveManual.Text = "Save Manual Trailer";
        _btnSaveManual.Width = 155;
        _btnSaveManual.Height = 36;
        _btnSaveManual.BackColor = Color.FromArgb(0, 120, 215);
        _btnSaveManual.ForeColor = Color.White;
        _btnSaveManual.FlatStyle = FlatStyle.Flat;
        _btnSaveManual.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);

        _btnCancel.Text = "Cancel";
        _btnCancel.Width = 95;
        _btnCancel.Height = 36;

        rightActions.Controls.Add(_btnSaveManual);
        rightActions.Controls.Add(_btnCancel);

        // Configure action tooltips
        _actionToolTip.SetToolTip(_btnResetToAuto, "Discards all manual segment customizations and resets this video to automatic trailer generation mode.");
        _actionToolTip.SetToolTip(_btnSaveManual, "Saves the current manual segments as this video's custom trailer configuration.");
        _actionToolTip.SetToolTip(_btnApplySavedConfig, "Applies the selected saved configuration preset to this video.");
        _actionToolTip.SetToolTip(_btnCancel, "Closes the editor without saving any changes.");

        actionPanel.Controls.Add(_btnResetToAuto, 0, 0);
        actionPanel.Controls.Add(savedConfigsPanel, 1, 0);
        actionPanel.Controls.Add(rightActions, 2, 0);
        rootLayout.Controls.Add(actionPanel, 0, 5);

        Controls.Add(rootLayout);
    }

    private void ConfigureSegmentsGrid()
    {
        _segmentsGrid.Dock = DockStyle.Fill;
        _segmentsGrid.AutoGenerateColumns = false;
        _segmentsGrid.AllowUserToAddRows = false;
        _segmentsGrid.AllowUserToDeleteRows = false;
        _segmentsGrid.ReadOnly = true;
        _segmentsGrid.MultiSelect = false;
        _segmentsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _segmentsGrid.RowHeadersVisible = false;
        _segmentsGrid.BackgroundColor = Color.White;
        _segmentsGrid.BorderStyle = BorderStyle.Fixed3D;

        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Segment #",
            Width = 90,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Start Time (mm:ss.f)",
            Width = 125,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Start (s)",
            Width = 95,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Duration (s)",
            Width = 95,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "End Time (mm:ss.f)",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private void InitializeEvents()
    {
        Shown += async (s, e) => await InitializeAnalysisAsync(forceReanalyze: false);

        // Transport
        _btnStepBack10.Click += (s, e) => SeekTo(_currentSeconds - 10.0, immediate: true);
        _btnStepBack1.Click += (s, e) => SeekTo(_currentSeconds - 1.0, immediate: true);
        _btnStepForward1.Click += (s, e) => SeekTo(_currentSeconds + 1.0, immediate: true);
        _btnStepForward10.Click += (s, e) => SeekTo(_currentSeconds + 10.0, immediate: true);
        _btnPlayPause.Click += (s, e) => TogglePlayPause();
        _btnAddSegmentHere.Click += (s, e) => AddSegmentAtCurrentPosition();
        _muteCheckBox.CheckedChanged += (s, e) => _isMuted = _muteCheckBox.Checked;
        _volumeTrackBar.ValueChanged += (s, e) => _lblVolume.Text = $"Vol: {_volumeTrackBar.Value}%";
        _chkCompareTrailer.CheckedChanged += async (s, e) => await HandleCompareTrailerCheckedChangedAsync();
        _btnPlayPauseTrailer.Click += (s, e) => ToggleTrailerPlayback();
        _trailerScrubber.Scroll += (s, e) =>
        {
            double sec = _trailerScrubber.Value / 10.0;
            _lblTrailerTime.Text = FormatTimestamp(sec);
            if (_isTrailerPlaying)
            {
                StartTrailerPlayback(sec);
            }
            else
            {
                _ = RequestTrailerFrameAsync(sec, immediate: false);
            }
        };
        _btnReanalyze.Click += async (s, e) => await HandleReanalyzeClickAsync();

        // Main Scrubber
        _videoScrollBar.Scroll += (s, e) =>
        {
            if (_isUpdatingUi) return;
            double sec = _videoScrollBar.Value / 10.0;
            SeekTo(sec, immediate: false, fromScroller: true);
        };

        // Timeline Painting
        _scenesCanvas.Paint += PaintScenesCanvas;
        _audioCanvas.Paint += PaintAudioCanvas;
        _movementCanvas.Paint += PaintMovementCanvas;
        _randomCanvas.Paint += PaintRandomCanvas;
        _manualCanvas.Paint += PaintManualCanvas;

        // Timeline Mouse Events
        _scenesCanvas.MouseClick += (s, e) => SeekTo(PixelToSeconds(e.X, _scenesCanvas.Width), immediate: true);
        _audioCanvas.MouseClick += HandleAudioCanvasClick;
        _movementCanvas.MouseClick += HandleMovementCanvasClick;
        _randomCanvas.MouseClick += HandleRandomCanvasClick;

        _manualCanvas.MouseDown += HandleManualCanvasMouseDown;
        _manualCanvas.MouseMove += HandleManualCanvasMouseMove;
        _manualCanvas.MouseUp += HandleManualCanvasMouseUp;

        // Mode action buttons
        _btnApplyAudio.Click += (s, e) => ApplyAudioModeSegments();
        _btnApplyMovement.Click += (s, e) => ApplyMovementModeSegments();
        _btnRerollRandom.Click += (s, e) => RerollRandomSegments();
        _btnApplyRandom.Click += (s, e) => ApplyRandomModeSegments();

        // Saved Configuration Presets
        _btnApplySavedConfig.Click += (s, e) => ApplySelectedSavedConfig();

        // Fine-tuning controls
        _segmentsGrid.SelectionChanged += SegmentsGrid_SelectionChanged;
        _segmentStartNumeric.ValueChanged += SegmentStartNumeric_ValueChanged;
        _segmentDurationNumeric.ValueChanged += SegmentDurationNumeric_ValueChanged;
        _setStartToCurrentButton.Click += (s, e) => SetSelectedStartToCurrent();
        _addSegmentAtScrollButton.Click += (s, e) => AddSegmentAtCurrentPosition();
        _removeSegmentButton.Click += (s, e) => RemoveSelectedSegment();

        // Dialog actions
        _btnSaveManual.Click += (s, e) => SaveManualTrailer();
        _btnResetToAuto.Click += (s, e) => ResetToAutomatic();
        _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
    }

    private async Task InitializeAnalysisAsync(bool forceReanalyze)
    {
        bool hadCachedAnalysis = _videoEntry.CachedAnalysis != null && !forceReanalyze;

        if (hadCachedAnalysis)
        {
            _statusLabel.Text = "Loading cached timeline analysis...";
        }
        else
        {
            _statusLabel.Text = forceReanalyze
                ? "Re-analyzing video from scratch..."
                : "Analyzing video scenes, audio peaks, and movement...";
        }
        _statusLabel.ForeColor = Color.FromArgb(0, 102, 204);
        SetControlsEnabled(false);

        try
        {
            _analysis = await _generatorService.AnalyzeVideoTimelineAsync(
                _baseOptions.FfmpegPath,
                _videoEntry.VideoPath,
                _baseOptions,
                UpdateStatusText,
                _formCts.Token,
                videoEntry: _videoEntry,
                forceReanalyze: forceReanalyze);

            if (_analysis == null || _analysis.DurationSeconds <= 0)
            {
                _statusLabel.Text = "Analysis failed: could not determine video duration.";
                _statusLabel.ForeColor = Color.Red;
                return;
            }

            _durationSeconds = _analysis.DurationSeconds;
            _videoScrollBar.Minimum = 0;
            _videoScrollBar.Maximum = Math.Max(1, (int)Math.Ceiling(_analysis.DurationSeconds * 10.0));
            _segmentStartNumeric.Maximum = (decimal)_analysis.DurationSeconds;

            if (!forceReanalyze && _videoEntry.IsManual && _videoEntry.ManualSegments.Count > 0)
            {
                _workingSegments = _videoEntry.ManualSegments.Select(s => s.Clone()).ToList();
            }
            else
            {
                _workingSegments = _baseOptions.Mode switch
                {
                    TrailerGenerationMode.SoundDetect => _analysis.AudioModeSegments.Select(s => s.Clone()).ToList(),
                    TrailerGenerationMode.MovementDetection => _analysis.MovementModeSegments.Select(s => s.Clone()).ToList(),
                    _ => _analysis.RandomModeSegments.Select(s => s.Clone()).ToList()
                };
            }

            PopulateSavedConfigurations();
            RefreshWorkingSegmentsUi();
            InvalidateAllTimelines();

            double initialSeek = _workingSegments.Count > 0
                ? _workingSegments[0].StartSeconds
                : _analysis.UsableStartSeconds;

            SeekTo(initialSeek, immediate: true);

            if (hadCachedAnalysis)
            {
                _statusLabel.Text = "Loaded cached timeline analysis (instant).";
            }
            else
            {
                _statusLabel.Text = "Analysis complete.";
            }
            _statusLabel.ForeColor = Color.FromArgb(40, 140, 60);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Analysis error: {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async Task HandleReanalyzeClickAsync()
    {
        if (_isPlaying)
        {
            StopPlayback();
        }

        var confirm = MessageBox.Show(
            this,
            "Re-analyze the video file from scratch? This will re-scan scene cuts, motion scores, and audio loudness.",
            "Re-analyze Video",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes)
        {
            return;
        }

        await InitializeAnalysisAsync(forceReanalyze: true);
    }

    private async Task HandleCompareTrailerCheckedChangedAsync()
    {
        if (_chkCompareTrailer.Checked)
        {
            _previewSplitContainer.ColumnStyles[0].Width = 50f;
            _previewSplitContainer.ColumnStyles[1].Width = 50f;
            await InitializeAndRenderTrailerPreviewAsync();
        }
        else
        {
            StopTrailerPlayback();
            _previewSplitContainer.ColumnStyles[0].Width = 100f;
            _previewSplitContainer.ColumnStyles[1].Width = 0f;
        }
    }

    private async Task InitializeAndRenderTrailerPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(_videoEntry.TrailerPath) || !File.Exists(_videoEntry.TrailerPath))
        {
            return;
        }

        if (_trailerDurationSeconds <= 0)
        {
            var dur = await _generatorService.GetVideoDurationSecondsAsync(_baseOptions.FfmpegPath, _videoEntry.TrailerPath, _formCts.Token);
            _trailerDurationSeconds = dur ?? 60.0;
            _trailerScrubber.Minimum = 0;
            _trailerScrubber.Maximum = Math.Max(1, (int)Math.Ceiling(_trailerDurationSeconds * 10.0));
        }

        double currentTrailerSec = _trailerScrubber.Value / 10.0;
        _lblTrailerTime.Text = FormatTimestamp(currentTrailerSec);
        await RequestTrailerFrameAsync(currentTrailerSec, immediate: true);
    }

    private async Task RequestTrailerFrameAsync(double timestamp, bool immediate)
    {
        try
        {
            _trailerScrubCts?.Cancel();
            _trailerScrubCts?.Dispose();
            _trailerScrubCts = null;
        }
        catch { }

        _trailerScrubCts = CancellationTokenSource.CreateLinkedTokenSource(_formCts.Token);
        var token = _trailerScrubCts.Token;

        try
        {
            if (!immediate)
            {
                await Task.Delay(60, token).ConfigureAwait(false);
            }

            var bytes = await _generatorService.ExtractFrameBytesAsync(
                _baseOptions.FfmpegPath,
                _videoEntry.TrailerPath,
                timestamp,
                640,
                token).ConfigureAwait(false);

            if (token.IsCancellationRequested || bytes == null || bytes.Length == 0)
            {
                return;
            }

            if (IsDisposed || Disposing) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateTrailerPreviewImage(bytes)));
            }
            else
            {
                UpdateTrailerPreviewImage(bytes);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on rapid scrubbing
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Trailer frame preview error: {ex.Message}");
        }
    }

    private void UpdateTrailerPreviewImage(byte[] bmpBytes)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            using var ms = new MemoryStream(bmpBytes);
            var newBmp = new Bitmap(ms);
            var oldImage = _existingTrailerPreviewBox.Image;
            _existingTrailerPreviewBox.Image = newBmp;
            oldImage?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Trailer image decode error: {ex.Message}");
        }
    }

    private void PopulateSavedConfigurations()
    {
        _savedConfigsComboBox.Items.Clear();

        lock (_videoEntry.SavedConfigurations)
        {
            if (_videoEntry.SavedConfigurations.Count > 0)
            {
                foreach (var key in _videoEntry.SavedConfigurations.Keys)
                {
                    _savedConfigsComboBox.Items.Add(key);
                }

                _savedConfigsComboBox.SelectedIndex = 0;
                _btnApplySavedConfig.Enabled = true;
            }
            else
            {
                _savedConfigsComboBox.Items.Add("(No saved configurations)");
                _savedConfigsComboBox.SelectedIndex = 0;
                _btnApplySavedConfig.Enabled = false;
            }
        }
    }

    private void ApplySelectedSavedConfig()
    {
        if (_savedConfigsComboBox.SelectedItem is string key &&
            _videoEntry.SavedConfigurations.TryGetValue(key, out var segs) &&
            segs != null)
        {
            _workingSegments = segs.Select(s => s.Clone()).ToList();
            RefreshWorkingSegmentsUi();
            if (_workingSegments.Count > 0)
            {
                SeekTo(_workingSegments[0].StartSeconds, immediate: true);
            }
            UpdateStatusText($"Applied saved config: {key}");
        }
    }

    private void UpdateStatusText(string msg)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateStatusText(msg)));
            return;
        }
        _statusLabel.Text = msg;
    }

    private void SetControlsEnabled(bool enabled)
    {
        _btnStepBack10.Enabled = enabled;
        _btnStepBack1.Enabled = enabled;
        _btnPlayPause.Enabled = enabled;
        _btnStepForward1.Enabled = enabled;
        _btnStepForward10.Enabled = enabled;
        _btnAddSegmentHere.Enabled = enabled;
        _muteCheckBox.Enabled = enabled;
        _volumeTrackBar.Enabled = enabled;
        bool hasTrailer = !string.IsNullOrWhiteSpace(_videoEntry.TrailerPath) && File.Exists(_videoEntry.TrailerPath);
        _chkCompareTrailer.Enabled = enabled && hasTrailer;
        _trailerScrubber.Enabled = enabled && hasTrailer;
        _btnReanalyze.Enabled = enabled;
        _videoScrollBar.Enabled = enabled;
        _btnApplyAudio.Enabled = enabled;
        _btnApplyMovement.Enabled = enabled;
        _btnRerollRandom.Enabled = enabled;
        _btnApplyRandom.Enabled = enabled;
        _savedConfigsComboBox.Enabled = enabled;
        _btnApplySavedConfig.Enabled = enabled && _videoEntry.SavedConfigurations.Count > 0;
        _btnSaveManual.Enabled = enabled;
        _btnResetToAuto.Enabled = enabled;
    }

    // -----------------------------------------------------------------
    // Smooth 24 FPS Playback & Seeking Logic
    // -----------------------------------------------------------------
    private void TogglePlayPause()
    {
        if (_isPlaying)
        {
            StopPlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    public void TogglePlayback() => TogglePlayPause();

    public void StartContinuousPlayback(int debounceMs = 0) => StartPlaybackFromCurrentTime(debounceMs);

    public void StopPlayback()
    {
        _isPlaying = false;

        try
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _playbackCts = null;
        }
        catch { }

        StopTrailerPlayback();

        Interlocked.Exchange(ref _uiFrameUpdateInFlight, 0);

        if (IsHandleCreated && !IsDisposed)
        {
            _btnPlayPause.Text = "▶ Play Preview";
            _btnPlayPause.BackColor = Color.FromArgb(40, 120, 65);
        }
    }

    public void ToggleTrailerPlayback()
    {
        if (_isTrailerPlaying)
        {
            StopTrailerPlayback();
        }
        else
        {
            StartTrailerPlayback();
        }
    }

    public void StartTrailerPlayback(double? seekSec = null)
    {
        if (string.IsNullOrWhiteSpace(_videoEntry.TrailerPath) || !File.Exists(_videoEntry.TrailerPath))
        {
            return;
        }

        double maxDuration = _trailerDurationSeconds > 0 ? _trailerDurationSeconds : (_trailerScrubber.Maximum / 10.0);
        double startSec = seekSec ?? (_trailerScrubber.Value / 10.0);

        if (maxDuration > 0 && startSec >= maxDuration - 0.2)
        {
            startSec = 0.0;
            _trailerScrubber.Value = 0;
            _lblTrailerTime.Text = FormatTimestamp(0.0);
        }

        double remainingSec = Math.Max(0.5, maxDuration - startSec);

        _isTrailerPlaying = true;
        _btnPlayPauseTrailer.Text = "⏸ Pause";
        _btnPlayPauseTrailer.BackColor = Color.FromArgb(190, 95, 20);

        try
        {
            _trailerScrubCts?.Cancel();
            _trailerScrubCts?.Dispose();
            _trailerScrubCts = null;
        }
        catch { }

        try
        {
            _trailerPlaybackCts?.Cancel();
            _trailerPlaybackCts?.Dispose();
            _trailerPlaybackCts = null;
        }
        catch { }

        _trailerPlaybackCts = CancellationTokenSource.CreateLinkedTokenSource(_formCts.Token);
        var token = _trailerPlaybackCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await _generatorService.StreamSmoothPreviewAsync(
                    _baseOptions.FfmpegPath,
                    _videoEntry.TrailerPath,
                    startSec,
                    remainingSec,
                    maxWidth: 640,
                    isMutedProvider: () => _chkMuteTrailer.Checked,
                    onFrameDecoded: (bmpBytes, timestamp) =>
                    {
                        if (token.IsCancellationRequested) return;
                        if (Interlocked.CompareExchange(ref _trailerFrameUpdateInFlight, 1, 0) != 0) return;

                        if (!IsHandleCreated || IsDisposed)
                        {
                            Interlocked.Exchange(ref _trailerFrameUpdateInFlight, 0);
                            return;
                        }

                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (!_isTrailerPlaying || token.IsCancellationRequested || IsDisposed || Disposing)
                                    {
                                        return;
                                    }

                                    int trackVal = Math.Clamp((int)Math.Round(timestamp * 10.0), 0, _trailerScrubber.Maximum);
                                    _trailerScrubber.Value = trackVal;
                                    _lblTrailerTime.Text = FormatTimestamp(timestamp);
                                    UpdateTrailerPreviewImage(bmpBytes);
                                }
                                finally
                                {
                                    Interlocked.Exchange(ref _trailerFrameUpdateInFlight, 0);
                                }
                            }));
                        }
                        catch
                        {
                            Interlocked.Exchange(ref _trailerFrameUpdateInFlight, 0);
                        }
                    },
                    cancellationToken: token,
                    volumeProvider: () => (float)(_volumeTrackBar.Value / 100.0f)).ConfigureAwait(false);

                if (!token.IsCancellationRequested && IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_isTrailerPlaying)
                        {
                            StopTrailerPlayback();
                        }
                    }));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when paused or scrubbed
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Trailer playback error: {ex.Message}");
            }
        }, token);
    }

    public void StopTrailerPlayback()
    {
        _isTrailerPlaying = false;

        try
        {
            _trailerPlaybackCts?.Cancel();
            _trailerPlaybackCts?.Dispose();
            _trailerPlaybackCts = null;
        }
        catch { }

        Interlocked.Exchange(ref _trailerFrameUpdateInFlight, 0);

        if (IsHandleCreated && !IsDisposed)
        {
            _btnPlayPauseTrailer.Text = "▶ Play";
            _btnPlayPauseTrailer.BackColor = Color.FromArgb(45, 45, 55);
        }
    }

    private void StartPlayback()
    {
        if (_analysis == null) return;

        _isPlaying = true;
        _btnPlayPause.Text = "⏸ Pause";
        _btnPlayPause.BackColor = Color.FromArgb(190, 95, 20);

        try
        {
            _scrubCts?.Cancel();
            _scrubCts?.Dispose();
            _scrubCts = null;
        }
        catch { }

        try
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _playbackCts = null;
        }
        catch { }

        StopTrailerPlayback();

        _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(_formCts.Token);
        var token = _playbackCts.Token;

        // Synchronized Dual-Player: Right side (Existing Trailer)
        if (_chkCompareTrailer.Checked && !string.IsNullOrWhiteSpace(_videoEntry.TrailerPath) && File.Exists(_videoEntry.TrailerPath))
        {
            double maxDur = _trailerDurationSeconds > 0 ? _trailerDurationSeconds : (_trailerScrubber.Maximum / 10.0);
            double trailerStart = _trailerScrubber.Value / 10.0;
            if (maxDur > 0 && trailerStart >= maxDur - 0.2)
            {
                trailerStart = 0.0;
                _trailerScrubber.Value = 0;
                _lblTrailerTime.Text = FormatTimestamp(0.0);
            }
            double trailerRemaining = Math.Max(0.5, maxDur - trailerStart);

            _isTrailerPlaying = true;
            _btnPlayPauseTrailer.Text = "⏸ Pause";
            _btnPlayPauseTrailer.BackColor = Color.FromArgb(190, 95, 20);

            _trailerPlaybackCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var trailerToken = _trailerPlaybackCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _generatorService.StreamSmoothPreviewAsync(
                        _baseOptions.FfmpegPath,
                        _videoEntry.TrailerPath,
                        trailerStart,
                        trailerRemaining,
                        maxWidth: 640,
                        isMutedProvider: () => _chkMuteTrailer.Checked,
                        onFrameDecoded: (bmpBytes, timestamp) =>
                        {
                            if (trailerToken.IsCancellationRequested) return;
                            if (Interlocked.CompareExchange(ref _trailerFrameUpdateInFlight, 1, 0) != 0) return;

                            if (!IsHandleCreated || IsDisposed)
                            {
                                Interlocked.Exchange(ref _trailerFrameUpdateInFlight, 0);
                                return;
                            }

                            try
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    try
                                    {
                                        if (!_isTrailerPlaying || trailerToken.IsCancellationRequested || IsDisposed || Disposing)
                                        {
                                            return;
                                        }

                                        int trackVal = Math.Clamp((int)Math.Round(timestamp * 10.0), 0, _trailerScrubber.Maximum);
                                        _trailerScrubber.Value = trackVal;
                                        _lblTrailerTime.Text = FormatTimestamp(timestamp);
                                        UpdateTrailerPreviewImage(bmpBytes);
                                    }
                                    finally
                                    {
                                        Interlocked.Exchange(ref _trailerFrameUpdateInFlight, 0);
                                    }
                                }));
                            }
                            catch
                            {
                                Interlocked.Exchange(ref _trailerFrameUpdateInFlight, 0);
                            }
                        },
                        cancellationToken: trailerToken,
                        volumeProvider: () => (float)(_volumeTrackBar.Value / 100.0f)).ConfigureAwait(false);

                    if (!trailerToken.IsCancellationRequested && IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _isTrailerPlaying = false;
                            _btnPlayPauseTrailer.Text = "▶ Play";
                            _btnPlayPauseTrailer.BackColor = Color.FromArgb(45, 45, 55);
                            // Hold final frame; do not stop new trailer
                        }));
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected on pause/stop
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Synchronized trailer error: {ex.Message}");
                }
            }, trailerToken);
        }

        // Left side (New Trailer / Manual Segments Sequence or Continuous)
        _ = Task.Run(async () =>
        {
            try
            {
                if (_workingSegments.Count > 0)
                {
                    var sortedSegments = _workingSegments.OrderBy(s => s.StartSeconds).ToList();
                    foreach (var seg in sortedSegments)
                    {
                        if (token.IsCancellationRequested) break;
                        if (seg.DurationSeconds <= 0.05) continue;

                        if (IsHandleCreated && !IsDisposed)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (token.IsCancellationRequested || IsDisposed || Disposing) return;
                                _currentSeconds = seg.StartSeconds;
                                _isUpdatingUi = true;
                                _videoScrollBar.Value = Math.Clamp((int)Math.Round(_currentSeconds * 10.0), 0, _videoScrollBar.Maximum);
                                _isUpdatingUi = false;
                                UpdateTimestampLabel();
                                InvalidateAllTimelines();
                            }));
                        }

                        await _generatorService.StreamSmoothPreviewAsync(
                            _baseOptions.FfmpegPath,
                            _videoEntry.VideoPath,
                            seg.StartSeconds,
                            seg.DurationSeconds,
                            maxWidth: 640,
                            isMutedProvider: () => _isMuted,
                            onFrameDecoded: (bmpBytes, timestamp) =>
                            {
                                if (token.IsCancellationRequested) return;
                                if (Interlocked.CompareExchange(ref _uiFrameUpdateInFlight, 1, 0) != 0) return;

                                if (!IsHandleCreated || IsDisposed)
                                {
                                    Interlocked.Exchange(ref _uiFrameUpdateInFlight, 0);
                                    return;
                                }

                                try
                                {
                                    BeginInvoke(new Action(() =>
                                    {
                                        try
                                        {
                                            if (!_isPlaying || token.IsCancellationRequested || IsDisposed || Disposing)
                                            {
                                                return;
                                            }

                                            _currentSeconds = Math.Clamp(timestamp, 0.0, _durationSeconds);

                                            _isUpdatingUi = true;
                                            int trackVal = Math.Clamp((int)Math.Round(_currentSeconds * 10.0), 0, _videoScrollBar.Maximum);
                                            _videoScrollBar.Value = trackVal;
                                            _isUpdatingUi = false;

                                            UpdateTimestampLabel();
                                            InvalidateAllTimelines();
                                            UpdatePreviewImage(bmpBytes);
                                        }
                                        finally
                                        {
                                            Interlocked.Exchange(ref _uiFrameUpdateInFlight, 0);
                                        }
                                    }));
                                }
                                catch
                                {
                                    Interlocked.Exchange(ref _uiFrameUpdateInFlight, 0);
                                }
                            },
                            cancellationToken: token,
                            volumeProvider: () => (float)(_volumeTrackBar.Value / 100.0f)).ConfigureAwait(false);
                    }

                    if (!token.IsCancellationRequested && IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (_isPlaying)
                            {
                                StopPlayback();
                            }
                        }));
                    }
                }
                else
                {
                    double startSec = _currentSeconds;
                    if (startSec >= _durationSeconds - 0.2)
                    {
                        startSec = 0.0;
                        if (IsHandleCreated && !IsDisposed)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                _currentSeconds = 0.0;
                                _isUpdatingUi = true;
                                _videoScrollBar.Value = 0;
                                _isUpdatingUi = false;
                                UpdateTimestampLabel();
                                InvalidateAllTimelines();
                            }));
                        }
                    }

                    await RunPlaybackLoopAsync(startSec, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on stop/pause
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Playback stream error: {ex.Message}");
            }
        }, token);
    }

    private void StartPlaybackFromCurrentTime(int debounceMs = 0)
    {
        if (_analysis == null) return;

        _isPlaying = true;
        _btnPlayPause.Text = "⏸ Pause";
        _btnPlayPause.BackColor = Color.FromArgb(190, 95, 20);

        try
        {
            _scrubCts?.Cancel();
            _scrubCts?.Dispose();
            _scrubCts = null;
        }
        catch { }

        try
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _playbackCts = null;
        }
        catch { }

        _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(_formCts.Token);
        var token = _playbackCts.Token;

        double startSec = _currentSeconds;
        if (startSec >= _durationSeconds - 0.2)
        {
            startSec = 0.0;
            _currentSeconds = 0.0;
            _isUpdatingUi = true;
            _videoScrollBar.Value = 0;
            _isUpdatingUi = false;
            UpdateTimestampLabel();
            InvalidateAllTimelines();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (debounceMs > 0)
                {
                    await Task.Delay(debounceMs, token).ConfigureAwait(false);
                }

                if (token.IsCancellationRequested) return;

                await RunPlaybackLoopAsync(startSec, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when seeking or stopping
            }
        }, token);
    }

    private async Task RunPlaybackLoopAsync(double startSec, CancellationToken token)
    {
        try
        {
            double remainingSec = Math.Max(0.5, _durationSeconds - startSec);

            await _generatorService.StreamSmoothPreviewAsync(
                _baseOptions.FfmpegPath,
                _videoEntry.VideoPath,
                startSec,
                remainingSec,
                maxWidth: 640,
                isMutedProvider: () => _isMuted,
                onFrameDecoded: (bmpBytes, timestamp) =>
                {
                    if (token.IsCancellationRequested) return;
                    if (Interlocked.CompareExchange(ref _uiFrameUpdateInFlight, 1, 0) != 0) return;

                    if (!IsHandleCreated || IsDisposed)
                    {
                        Interlocked.Exchange(ref _uiFrameUpdateInFlight, 0);
                        return;
                    }

                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (!_isPlaying || token.IsCancellationRequested || IsDisposed || Disposing)
                                {
                                    return;
                                }

                                _currentSeconds = Math.Clamp(timestamp, 0.0, _durationSeconds);

                                _isUpdatingUi = true;
                                int trackVal = Math.Clamp((int)Math.Round(_currentSeconds * 10.0), 0, _videoScrollBar.Maximum);
                                _videoScrollBar.Value = trackVal;
                                _isUpdatingUi = false;

                                UpdateTimestampLabel();
                                InvalidateAllTimelines();
                                UpdatePreviewImage(bmpBytes);
                            }
                            finally
                            {
                                Interlocked.Exchange(ref _uiFrameUpdateInFlight, 0);
                            }
                        }));
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _uiFrameUpdateInFlight, 0);
                    }
                },
                cancellationToken: token,
                volumeProvider: () => (float)(_volumeTrackBar.Value / 100.0f));

            if (!token.IsCancellationRequested)
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_isPlaying)
                        {
                            StopPlayback();
                        }
                    }));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Playback stream error: {ex.Message}");
        }
    }

    private void SeekTo(double seconds, bool immediate = false, bool fromScroller = false)
    {
        if (_analysis == null) return;

        double maxSec = Math.Max(0.0, _durationSeconds);
        _currentSeconds = Math.Clamp(seconds, 0.0, maxSec);

        int trackVal = Math.Clamp((int)Math.Round(_currentSeconds * 10.0), 0, _videoScrollBar.Maximum);
        if (_videoScrollBar.Value != trackVal)
        {
            _isUpdatingUi = true;
            _videoScrollBar.Value = trackVal;
            _isUpdatingUi = false;
        }

        UpdateTimestampLabel();
        InvalidateAllTimelines();

        if (_isPlaying)
        {
            StartPlaybackFromCurrentTime(debounceMs: fromScroller ? 80 : 0);
        }
        else
        {
            _ = RequestFramePreviewAsync(_currentSeconds, immediate);
        }
    }

    private async Task RequestFramePreviewAsync(double timestamp, bool immediate)
    {
        try
        {
            _scrubCts?.Cancel();
            _scrubCts?.Dispose();
        }
        catch { }

        _scrubCts = CancellationTokenSource.CreateLinkedTokenSource(_formCts.Token);
        var token = _scrubCts.Token;

        try
        {
            if (!immediate)
            {
                await Task.Delay(60, token);
            }

            var bytes = await _generatorService.ExtractFrameBytesAsync(
                _baseOptions.FfmpegPath,
                _videoEntry.VideoPath,
                timestamp,
                720,
                token);

            if (token.IsCancellationRequested || bytes == null || bytes.Length == 0)
            {
                return;
            }

            if (IsDisposed || Disposing) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdatePreviewImage(bytes)));
            }
            else
            {
                UpdatePreviewImage(bytes);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when scrubbing quickly
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Frame preview extraction error: {ex.Message}");
        }
    }

    private void UpdatePreviewImage(byte[] bmpBytes)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            using var ms = new MemoryStream(bmpBytes);
            var newBmp = new Bitmap(ms);
            var oldImage = _videoPreviewBox.Image;
            _videoPreviewBox.Image = newBmp;
            oldImage?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Preview image decode error: {ex.Message}");
        }
    }

    private void UpdateTimestampLabel()
    {
        _timestampReadoutLabel.Text = $"{FormatTimestamp(_currentSeconds)} / {FormatTimestamp(_durationSeconds)}";
    }

    private void InvalidateAllTimelines()
    {
        _scenesCanvas.Invalidate();
        _audioCanvas.Invalidate();
        _movementCanvas.Invalidate();
        _randomCanvas.Invalidate();
        _manualCanvas.Invalidate();
    }

    // -----------------------------------------------------------------
    // Timeline Coordinate Mapping
    // -----------------------------------------------------------------
    private int SecondsToPixel(double seconds, int canvasWidth)
    {
        if (_durationSeconds <= 0) return TimelineMargin;
        int usableWidth = Math.Max(1, canvasWidth - (TimelineMargin * 2));
        double fraction = Math.Clamp(seconds / _durationSeconds, 0.0, 1.0);
        return TimelineMargin + (int)Math.Round(fraction * usableWidth);
    }

    private double PixelToSeconds(int pixelX, int canvasWidth)
    {
        if (_durationSeconds <= 0) return 0.0;
        int usableWidth = Math.Max(1, canvasWidth - (TimelineMargin * 2));
        int relativeX = Math.Clamp(pixelX - TimelineMargin, 0, usableWidth);
        double fraction = (double)relativeX / usableWidth;
        return Math.Clamp(fraction * _durationSeconds, 0.0, _durationSeconds);
    }

    // -----------------------------------------------------------------
    // Timeline Owner-Drawn Rendering
    // -----------------------------------------------------------------
    private void PaintScenesCanvas(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = _scenesCanvas.Width;
        int h = _scenesCanvas.Height;

        g.Clear(Color.FromArgb(24, 26, 32));
        if (_analysis == null) return;

        // Scenes
        for (int i = 0; i < _analysis.Scenes.Count; i++)
        {
            var (sStart, sEnd) = _analysis.Scenes[i];
            int x1 = SecondsToPixel(sStart, w);
            int x2 = SecondsToPixel(sEnd, w);
            int segW = Math.Max(1, x2 - x1);

            Color fill = (i % 2 == 0) ? Color.FromArgb(50, 60, 75) : Color.FromArgb(38, 46, 58);
            using var brush = new SolidBrush(fill);
            g.FillRectangle(brush, x1, 2, segW, h - 4);
        }

        // Shaded Start Skip
        int xStartSkip = SecondsToPixel(_analysis.UsableStartSeconds, w);
        if (xStartSkip > TimelineMargin)
        {
            using var skipBrush = new SolidBrush(Color.FromArgb(180, 30, 32, 38));
            g.FillRectangle(skipBrush, TimelineMargin, 0, xStartSkip - TimelineMargin, h);
            if (xStartSkip - TimelineMargin > 40)
            {
                using var font = new Font("Segoe UI", 7.5F);
                g.DrawString("Start Skip", font, Brushes.Silver, TimelineMargin + 4, 3);
            }
        }

        // Shaded End Skip
        int xEndSkip = SecondsToPixel(_analysis.UsableEndSeconds, w);
        int xMax = SecondsToPixel(_durationSeconds, w);
        if (xMax > xEndSkip)
        {
            using var skipBrush = new SolidBrush(Color.FromArgb(180, 30, 32, 38));
            g.FillRectangle(skipBrush, xEndSkip, 0, xMax - xEndSkip, h);
            if (xMax - xEndSkip > 40)
            {
                using var font = new Font("Segoe UI", 7.5F);
                g.DrawString("End Skip", font, Brushes.Silver, xEndSkip + 4, 3);
            }
        }

        // Scene Cuts
        using var cutPen = new Pen(Color.FromArgb(255, 215, 0), 2);
        foreach (var cut in _analysis.SceneCutTimestamps)
        {
            int cx = SecondsToPixel(cut, w);
            g.DrawLine(cutPen, cx, 0, cx, h);
        }

        DrawPlayhead(g, w, h);
    }

    private void PaintAudioCanvas(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = _audioCanvas.Width;
        int h = _audioCanvas.Height;

        g.Clear(Color.FromArgb(24, 26, 32));
        if (_analysis == null) return;

        using var fillBrush = new SolidBrush(Color.FromArgb(0, 150, 214));
        using var borderPen = new Pen(Color.FromArgb(120, 220, 255), 1);
        using var peakPen = new Pen(Color.White, 2);
        using var textFont = new Font("Segoe UI", 7.5F, FontStyle.Bold);

        for (int i = 0; i < _analysis.AudioModeSegments.Count; i++)
        {
            var seg = _analysis.AudioModeSegments[i];
            int x1 = SecondsToPixel(seg.StartSeconds, w);
            int x2 = SecondsToPixel(seg.EndSeconds, w);
            int segW = Math.Max(2, x2 - x1);

            g.FillRectangle(fillBrush, x1, 2, segW, h - 4);
            g.DrawRectangle(borderPen, x1, 2, segW, h - 4);

            int midX = (x1 + x2) / 2;
            g.DrawLine(peakPen, midX, 4, midX, h - 4);

            if (segW > 35)
            {
                g.DrawString($"#{i + 1}", textFont, Brushes.White, x1 + 2, 3);
            }
        }

        DrawPlayhead(g, w, h);
    }

    private void PaintMovementCanvas(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = _movementCanvas.Width;
        int h = _movementCanvas.Height;

        g.Clear(Color.FromArgb(24, 26, 32));
        if (_analysis == null) return;

        using var fillBrush = new SolidBrush(Color.FromArgb(235, 120, 20));
        using var borderPen = new Pen(Color.FromArgb(255, 180, 80), 1);
        using var peakPen = new Pen(Color.White, 2);
        using var textFont = new Font("Segoe UI", 7.5F, FontStyle.Bold);

        for (int i = 0; i < _analysis.MovementModeSegments.Count; i++)
        {
            var seg = _analysis.MovementModeSegments[i];
            int x1 = SecondsToPixel(seg.StartSeconds, w);
            int x2 = SecondsToPixel(seg.EndSeconds, w);
            int segW = Math.Max(2, x2 - x1);

            g.FillRectangle(fillBrush, x1, 2, segW, h - 4);
            g.DrawRectangle(borderPen, x1, 2, segW, h - 4);

            int midX = (x1 + x2) / 2;
            g.DrawLine(peakPen, midX, 4, midX, h - 4);

            if (segW > 35)
            {
                g.DrawString($"#{i + 1}", textFont, Brushes.White, x1 + 2, 3);
            }
        }

        DrawPlayhead(g, w, h);
    }

    private void PaintRandomCanvas(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = _randomCanvas.Width;
        int h = _randomCanvas.Height;

        g.Clear(Color.FromArgb(24, 26, 32));
        if (_analysis == null) return;

        using var fillBrush = new SolidBrush(Color.FromArgb(142, 68, 173));
        using var borderPen = new Pen(Color.FromArgb(195, 140, 230), 1);
        using var textFont = new Font("Segoe UI", 7.5F, FontStyle.Bold);

        for (int i = 0; i < _analysis.RandomModeSegments.Count; i++)
        {
            var seg = _analysis.RandomModeSegments[i];
            int x1 = SecondsToPixel(seg.StartSeconds, w);
            int x2 = SecondsToPixel(seg.EndSeconds, w);
            int segW = Math.Max(2, x2 - x1);

            g.FillRectangle(fillBrush, x1, 2, segW, h - 4);
            g.DrawRectangle(borderPen, x1, 2, segW, h - 4);

            if (segW > 35)
            {
                g.DrawString($"#{i + 1}", textFont, Brushes.White, x1 + 2, 3);
            }
        }

        DrawPlayhead(g, w, h);
    }

    private void PaintManualCanvas(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = _manualCanvas.Width;
        int h = _manualCanvas.Height;

        g.Clear(Color.FromArgb(18, 20, 26));

        using var defaultFill = new SolidBrush(Color.FromArgb(39, 140, 80));
        using var selectedFill = new SolidBrush(Color.FromArgb(46, 204, 113));
        using var defaultBorder = new Pen(Color.FromArgb(100, 210, 140), 1);
        using var selectedBorder = new Pen(Color.FromArgb(255, 235, 59), 2);
        using var textFont = new Font("Segoe UI", 8F, FontStyle.Bold);

        for (int i = 0; i < _workingSegments.Count; i++)
        {
            var seg = _workingSegments[i];
            int x1 = SecondsToPixel(seg.StartSeconds, w);
            int x2 = SecondsToPixel(seg.EndSeconds, w);
            int segW = Math.Max(4, x2 - x1);

            bool isSel = (i == _selectedSegmentIndex);
            g.FillRectangle(isSel ? selectedFill : defaultFill, x1, 3, segW, h - 6);
            g.DrawRectangle(isSel ? selectedBorder : defaultBorder, x1, 3, segW, h - 6);

            if (segW > 36)
            {
                g.DrawString($"#{i + 1} ({seg.DurationSeconds:F1}s)", textFont, Brushes.White, x1 + 3, 4);
            }
        }

        DrawPlayhead(g, w, h, drawHandle: true);
    }

    private void DrawPlayhead(Graphics g, int w, int h, bool drawHandle = false)
    {
        if (_durationSeconds <= 0) return;
        int headX = SecondsToPixel(_currentSeconds, w);

        using var headPen = new Pen(Color.FromArgb(255, 65, 65), 2);
        g.DrawLine(headPen, headX, 0, headX, h);

        if (drawHandle)
        {
            var triangle = new[]
            {
                new Point(headX - 5, 0),
                new Point(headX + 5, 0),
                new Point(headX, 6)
            };
            g.FillPolygon(Brushes.Red, triangle);
        }
    }

    // -----------------------------------------------------------------
    // Timeline Click & Drag Interaction Handlers
    // -----------------------------------------------------------------
    private void HandleAudioCanvasClick(object? sender, MouseEventArgs e)
    {
        if (_analysis == null) return;
        int w = _audioCanvas.Width;

        for (int i = 0; i < _analysis.AudioModeSegments.Count; i++)
        {
            var seg = _analysis.AudioModeSegments[i];
            int x1 = SecondsToPixel(seg.StartSeconds, w);
            int x2 = SecondsToPixel(seg.EndSeconds, w);
            if (e.X >= x1 && e.X <= x2)
            {
                SeekTo(seg.StartSeconds, immediate: true);
                return;
            }
        }

        SeekTo(PixelToSeconds(e.X, w), immediate: true);
    }

    private void HandleMovementCanvasClick(object? sender, MouseEventArgs e)
    {
        if (_analysis == null) return;
        int w = _movementCanvas.Width;

        for (int i = 0; i < _analysis.MovementModeSegments.Count; i++)
        {
            var seg = _analysis.MovementModeSegments[i];
            int x1 = SecondsToPixel(seg.StartSeconds, w);
            int x2 = SecondsToPixel(seg.EndSeconds, w);
            if (e.X >= x1 && e.X <= x2)
            {
                SeekTo(seg.StartSeconds, immediate: true);
                return;
            }
        }

        SeekTo(PixelToSeconds(e.X, w), immediate: true);
    }

    private void HandleRandomCanvasClick(object? sender, MouseEventArgs e)
    {
        if (_analysis == null) return;
        int w = _randomCanvas.Width;

        for (int i = 0; i < _analysis.RandomModeSegments.Count; i++)
        {
            var seg = _analysis.RandomModeSegments[i];
            int x1 = SecondsToPixel(seg.StartSeconds, w);
            int x2 = SecondsToPixel(seg.EndSeconds, w);
            if (e.X >= x1 && e.X <= x2)
            {
                SeekTo(seg.StartSeconds, immediate: true);
                return;
            }
        }

        SeekTo(PixelToSeconds(e.X, w), immediate: true);
    }

    private void HandleManualCanvasMouseDown(object? sender, MouseEventArgs e)
    {
        int w = _manualCanvas.Width;

        for (int i = 0; i < _workingSegments.Count; i++)
        {
            var seg = _workingSegments[i];
            int x1 = SecondsToPixel(seg.StartSeconds, w);
            int x2 = SecondsToPixel(seg.EndSeconds, w);

            if (e.X >= x1 && e.X <= x2)
            {
                _isDraggingSegment = true;
                _draggedSegmentIndex = i;
                _selectedSegmentIndex = i;
                _dragOffsetSeconds = PixelToSeconds(e.X, w) - seg.StartSeconds;
                _manualCanvas.Capture = true;

                SelectSegmentInGrid(i);
                SeekTo(seg.StartSeconds, immediate: true);
                _manualCanvas.Invalidate();
                return;
            }
        }

        SeekTo(PixelToSeconds(e.X, w), immediate: true);
    }

    private void HandleManualCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        int w = _manualCanvas.Width;

        if (_isDraggingSegment && _draggedSegmentIndex >= 0 && _draggedSegmentIndex < _workingSegments.Count)
        {
            double curMouseSec = PixelToSeconds(e.X, w);
            double newStart = curMouseSec - _dragOffsetSeconds;
            double segDur = _workingSegments[_draggedSegmentIndex].DurationSeconds;
            double maxStart = Math.Max(0.0, _durationSeconds - segDur);
            newStart = Math.Clamp(newStart, 0.0, maxStart);
            newStart = Math.Round(newStart, 1);

            _workingSegments[_draggedSegmentIndex].StartSeconds = newStart;

            _isUpdatingUi = true;
            _segmentStartNumeric.Value = (decimal)newStart;
            _isUpdatingUi = false;

            UpdateGridRowValues(_draggedSegmentIndex);
            SeekTo(newStart, immediate: false);
            _manualCanvas.Invalidate();
        }
        else
        {
            bool overSeg = false;
            for (int i = 0; i < _workingSegments.Count; i++)
            {
                var seg = _workingSegments[i];
                int x1 = SecondsToPixel(seg.StartSeconds, w);
                int x2 = SecondsToPixel(seg.EndSeconds, w);
                if (e.X >= x1 && e.X <= x2)
                {
                    overSeg = true;
                    break;
                }
            }
            _manualCanvas.Cursor = overSeg ? Cursors.Hand : Cursors.Default;
        }
    }

    private void HandleManualCanvasMouseUp(object? sender, MouseEventArgs e)
    {
        if (_isDraggingSegment && _draggedSegmentIndex >= 0 && _draggedSegmentIndex < _workingSegments.Count)
        {
            var draggedItem = _workingSegments[_draggedSegmentIndex];
            _isDraggingSegment = false;
            _draggedSegmentIndex = -1;
            _manualCanvas.Capture = false;

            _workingSegments.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
            _selectedSegmentIndex = _workingSegments.IndexOf(draggedItem);
            RefreshWorkingSegmentsUi();
        }
    }

    // -----------------------------------------------------------------
    // Mode Application & Regeneration
    // -----------------------------------------------------------------
    private void ApplyAudioModeSegments()
    {
        if (_analysis?.AudioModeSegments == null) return;
        _workingSegments = _analysis.AudioModeSegments.Select(s => s.Clone()).ToList();
        RefreshWorkingSegmentsUi();
        if (_workingSegments.Count > 0)
        {
            SeekTo(_workingSegments[0].StartSeconds, immediate: true);
        }
    }

    private void ApplyMovementModeSegments()
    {
        if (_analysis?.MovementModeSegments == null) return;
        _workingSegments = _analysis.MovementModeSegments.Select(s => s.Clone()).ToList();
        RefreshWorkingSegmentsUi();
        if (_workingSegments.Count > 0)
        {
            SeekTo(_workingSegments[0].StartSeconds, immediate: true);
        }
    }

    private void RerollRandomSegments()
    {
        if (_analysis == null) return;
        _analysis.RandomModeSegments = _generatorService.RegenerateRandomSegments(
            _analysis.Scenes,
            _analysis.DurationSeconds,
            _baseOptions.TargetDurationSeconds);
        _randomCanvas.Invalidate();
    }

    private void ApplyRandomModeSegments()
    {
        if (_analysis?.RandomModeSegments == null) return;
        _workingSegments = _analysis.RandomModeSegments.Select(s => s.Clone()).ToList();
        RefreshWorkingSegmentsUi();
        if (_workingSegments.Count > 0)
        {
            SeekTo(_workingSegments[0].StartSeconds, immediate: true);
        }
    }

    // -----------------------------------------------------------------
    // Working Segments Management & UI Refresh
    // -----------------------------------------------------------------
    private void RefreshWorkingSegmentsUi()
    {
        _isUpdatingUi = true;
        _segmentsGrid.Rows.Clear();

        for (int i = 0; i < _workingSegments.Count; i++)
        {
            var seg = _workingSegments[i];
            _segmentsGrid.Rows.Add(
                $"#{i + 1}",
                FormatTimestamp(seg.StartSeconds),
                seg.StartSeconds.ToString("F1", CultureInfo.InvariantCulture),
                seg.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture),
                FormatTimestamp(seg.EndSeconds)
            );
        }

        if (_workingSegments.Count > 0)
        {
            if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= _workingSegments.Count)
            {
                _selectedSegmentIndex = 0;
            }

            _segmentsGrid.Rows[_selectedSegmentIndex].Selected = true;
            var sel = _workingSegments[_selectedSegmentIndex];
            _segmentStartNumeric.Value = Math.Clamp((decimal)sel.StartSeconds, _segmentStartNumeric.Minimum, _segmentStartNumeric.Maximum);
            _segmentDurationNumeric.Value = Math.Clamp((decimal)sel.DurationSeconds, _segmentDurationNumeric.Minimum, _segmentDurationNumeric.Maximum);

            _segmentStartNumeric.Enabled = true;
            _segmentDurationNumeric.Enabled = true;
            _setStartToCurrentButton.Enabled = true;
            _removeSegmentButton.Enabled = true;
        }
        else
        {
            _selectedSegmentIndex = -1;
            _segmentStartNumeric.Enabled = false;
            _segmentDurationNumeric.Enabled = false;
            _setStartToCurrentButton.Enabled = false;
            _removeSegmentButton.Enabled = false;
        }

        _isUpdatingUi = false;

        UpdateSummaryText();
        _manualCanvas.Invalidate();
    }

    private void SelectSegmentInGrid(int index)
    {
        if (index < 0 || index >= _segmentsGrid.Rows.Count) return;

        _isUpdatingUi = true;
        _segmentsGrid.ClearSelection();
        _segmentsGrid.Rows[index].Selected = true;

        var seg = _workingSegments[index];
        _segmentStartNumeric.Value = Math.Clamp((decimal)seg.StartSeconds, _segmentStartNumeric.Minimum, _segmentStartNumeric.Maximum);
        _segmentDurationNumeric.Value = Math.Clamp((decimal)seg.DurationSeconds, _segmentDurationNumeric.Minimum, _segmentDurationNumeric.Maximum);
        _isUpdatingUi = false;
    }

    private void UpdateGridRowValues(int index)
    {
        if (index < 0 || index >= _segmentsGrid.Rows.Count || index >= _workingSegments.Count)
            return;

        var seg = _workingSegments[index];
        var row = _segmentsGrid.Rows[index];
        row.Cells[0].Value = $"#{index + 1}";
        row.Cells[1].Value = FormatTimestamp(seg.StartSeconds);
        row.Cells[2].Value = seg.StartSeconds.ToString("F1", CultureInfo.InvariantCulture);
        row.Cells[3].Value = seg.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture);
        row.Cells[4].Value = FormatTimestamp(seg.EndSeconds);
    }

    private void UpdateSummaryText()
    {
        double totalSec = _workingSegments.Sum(s => s.DurationSeconds);
        _summaryLabel.Text = $"Segments: {_workingSegments.Count} | Total Duration: {totalSec:F1}s / Target: {_baseOptions.TargetDurationSeconds}s";
    }

    private void SegmentsGrid_SelectionChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingUi) return;

        if (_segmentsGrid.SelectedRows.Count > 0)
        {
            int index = _segmentsGrid.SelectedRows[0].Index;
            if (index >= 0 && index < _workingSegments.Count && index != _selectedSegmentIndex)
            {
                _selectedSegmentIndex = index;
                var seg = _workingSegments[index];

                _isUpdatingUi = true;
                _segmentStartNumeric.Value = Math.Clamp((decimal)seg.StartSeconds, _segmentStartNumeric.Minimum, _segmentStartNumeric.Maximum);
                _segmentDurationNumeric.Value = Math.Clamp((decimal)seg.DurationSeconds, _segmentDurationNumeric.Minimum, _segmentDurationNumeric.Maximum);
                _isUpdatingUi = false;

                _manualCanvas.Invalidate();
                SeekTo(seg.StartSeconds, immediate: true);
            }
        }
    }

    private void SegmentStartNumeric_ValueChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingUi || _selectedSegmentIndex < 0 || _selectedSegmentIndex >= _workingSegments.Count)
            return;

        var seg = _workingSegments[_selectedSegmentIndex];
        seg.StartSeconds = (double)_segmentStartNumeric.Value;
        UpdateGridRowValues(_selectedSegmentIndex);
        _manualCanvas.Invalidate();
        SeekTo(seg.StartSeconds, immediate: true);
    }

    private void SegmentDurationNumeric_ValueChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingUi || _selectedSegmentIndex < 0 || _selectedSegmentIndex >= _workingSegments.Count)
            return;

        var seg = _workingSegments[_selectedSegmentIndex];
        seg.DurationSeconds = (double)_segmentDurationNumeric.Value;
        UpdateGridRowValues(_selectedSegmentIndex);
        UpdateSummaryText();
        _manualCanvas.Invalidate();
    }

    private void SetSelectedStartToCurrent()
    {
        if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= _workingSegments.Count)
        {
            AddSegmentAtCurrentPosition();
            return;
        }

        var seg = _workingSegments[_selectedSegmentIndex];
        double maxStart = Math.Max(0.0, _durationSeconds - seg.DurationSeconds);
        seg.StartSeconds = Math.Clamp(_currentSeconds, 0.0, maxStart);

        _isUpdatingUi = true;
        _segmentStartNumeric.Value = (decimal)seg.StartSeconds;
        _isUpdatingUi = false;

        UpdateGridRowValues(_selectedSegmentIndex);
        _manualCanvas.Invalidate();
    }

    private void AddSegmentAtCurrentPosition()
    {
        double dur = (double)_segmentDurationNumeric.Value;
        if (dur <= 0.5) dur = 5.0;

        double maxStart = Math.Max(0.0, _durationSeconds - dur);
        double start = Math.Clamp(_currentSeconds, 0.0, maxStart);

        var newSeg = new ManualSegmentItem
        {
            StartSeconds = Math.Round(start, 1),
            DurationSeconds = Math.Round(dur, 1)
        };

        _workingSegments.Add(newSeg);
        _workingSegments.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        _selectedSegmentIndex = _workingSegments.IndexOf(newSeg);

        RefreshWorkingSegmentsUi();
        SeekTo(newSeg.StartSeconds, immediate: true);
    }

    private void RemoveSelectedSegment()
    {
        if (_selectedSegmentIndex < 0 || _selectedSegmentIndex >= _workingSegments.Count)
            return;

        _workingSegments.RemoveAt(_selectedSegmentIndex);
        if (_selectedSegmentIndex >= _workingSegments.Count)
        {
            _selectedSegmentIndex = _workingSegments.Count - 1;
        }

        RefreshWorkingSegmentsUi();
    }

    // -----------------------------------------------------------------
    // Save / Reset / Form Closing Actions
    // -----------------------------------------------------------------
    private void SaveManualTrailer()
    {
        StopPlayback();

        if (_workingSegments.Count == 0)
        {
            var confirm = MessageBox.Show(
                this,
                "No manual segments are defined. Do you want to reset this video to automatic generation instead?",
                "No Segments Defined",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (confirm == DialogResult.Yes)
            {
                _videoEntry.IsManual = false;
                _videoEntry.ManualSegments.Clear();
                _videoEntry.NotifyStatusChanged();
                ResetToAutomaticRequested = true;
                SavedAsManual = false;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            return;
        }

        _videoEntry.ManualSegments = _workingSegments.Select(s => s.Clone()).ToList();
        _videoEntry.IsManual = true;
        _videoEntry.StatusMessage = "Manual Configured";

        // Save preset to SavedConfigurations
        string manualKey = $"Manual ({_workingSegments.Count} segments, {_workingSegments.Sum(s => s.DurationSeconds):F1}s)";
        lock (_videoEntry.SavedConfigurations)
        {
            _videoEntry.SavedConfigurations[manualKey] = _workingSegments.Select(s => s.Clone()).ToList();
        }

        _videoEntry.NotifyStatusChanged();

        SavedAsManual = true;
        ResetToAutomaticRequested = false;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ResetToAutomatic()
    {
        StopPlayback();

        var confirm = MessageBox.Show(
            this,
            "Are you sure you want to discard all manual segments and reset this video to Automatic generation mode?",
            "Reset to Automatic Mode",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        _videoEntry.IsManual = false;
        _videoEntry.ManualSegments.Clear();
        _videoEntry.NotifyStatusChanged();

        ResetToAutomaticRequested = true;
        SavedAsManual = false;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);

        StopPlayback();
        StopTrailerPlayback();

        try
        {
            _trailerPlaybackCts?.Cancel();
            _trailerPlaybackCts?.Dispose();
            _trailerPlaybackCts = null;
        }
        catch { }

        try
        {
            _trailerScrubCts?.Cancel();
            _trailerScrubCts?.Dispose();
            _trailerScrubCts = null;
        }
        catch { }

        try
        {
            _scrubCts?.Cancel();
            _scrubCts?.Dispose();
            _scrubCts = null;
        }
        catch { }

        try
        {
            _formCts.Cancel();
            _formCts.Dispose();
        }
        catch { }

        try
        {
            _actionToolTip.Dispose();
        }
        catch { }

        var oldImg = _videoPreviewBox.Image;
        _videoPreviewBox.Image = null;
        oldImg?.Dispose();

        var oldTrailerImg = _existingTrailerPreviewBox.Image;
        _existingTrailerPreviewBox.Image = null;
        oldTrailerImg?.Dispose();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------
    private static void ConfigureButton(Button btn, string text, int width, bool isPrimary = false)
    {
        btn.Text = text;
        btn.Width = width;
        btn.Height = 30;
        btn.Margin = new Padding(2, 2, 2, 2);
        if (isPrimary)
        {
            btn.BackColor = Color.FromArgb(0, 120, 215);
            btn.ForeColor = Color.White;
            btn.FlatStyle = FlatStyle.Flat;
            btn.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        }
    }

    private static void ConfigureSmallActionButton(Button btn, string text, int width)
    {
        btn.Text = text;
        btn.Width = width;
        btn.Height = 24;
        btn.Font = new Font("Segoe UI", 8F);
        btn.Margin = new Padding(2, 4, 2, 2);
    }

    private static Label CreateTrackLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 40, 40)
        };
    }

    private static void ConfigureTimelineCanvas(DoubleBufferedPanel canvas)
    {
        canvas.Dock = DockStyle.Fill;
        canvas.Margin = new Padding(0, 1, 0, 1);
        canvas.BackColor = Color.FromArgb(24, 26, 32);
        canvas.BorderStyle = BorderStyle.FixedSingle;
    }

    private static string FormatTimestamp(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100:D1}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100:D1}";
    }

    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UpdateStyles();
        }
    }

    private sealed class DoubleBufferedDataGridView : DataGridView
    {
        public DoubleBufferedDataGridView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();
        }
    }
}
