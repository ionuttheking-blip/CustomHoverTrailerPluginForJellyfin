using System.ComponentModel;
using TrailerGenerator.WinForms.Models;
using TrailerGenerator.WinForms.Services;

namespace TrailerGenerator.WinForms;

/// <summary>
/// Main Windows Forms UI for scanning folders and batch-generating *name_of_video*-trailer.mp4 files.
/// </summary>
public sealed class MainForm : Form
{
    private readonly VideoScannerService _scanner = new();
    private readonly FfmpegGeneratorService _generator = new();
    private readonly AppStatePersistenceService _stateService = new();

    private List<VideoEntry> _allVideos = new();
    private SortableBindingList<VideoEntry> _videos = new();
    private bool _isLoadingState = false;

    private string? _lastSortPropertyName;
    private ListSortDirection _lastSortDirection = ListSortDirection.Ascending;

    // Top panel controls
    private readonly ListBox _folderListBox = new();
    private ListBox _foldersListBox => _folderListBox; // Backward-compatibility alias
    private readonly TextBox _ffmpegPathTextBox = new();
    private readonly Button _addFolderButton = new();
    private readonly Button _removeFolderButton = new();

    // Section 2 controls
    private readonly ComboBox _modeComboBox = new();
    private readonly Label _modeDescriptionLabel = new();
    private readonly NumericUpDown _durationNumeric = new();
    private readonly NumericUpDown _snippetsNumeric = new();
    private readonly CheckBox _useSceneChangeCheckBox = new();
    private readonly RadioButton _usePercentRadio = new();
    private readonly NumericUpDown _startPercentNumeric = new();
    private readonly RadioButton _useFixedSecRadio = new();
    private readonly NumericUpDown _fixedSecNumeric = new();
    private readonly NumericUpDown _endPercentNumeric = new();
    private readonly NumericUpDown _parallelCountNumeric = new();
    private readonly ComboBox _resolutionComboBox = new();
    private readonly CheckBox _audioCheckBox = new();
    private readonly CheckBox _fadeCheckBox = new();
    private readonly CheckBox _overwriteCheckBox = new();
    private readonly CheckBox _overrideManualCheckBox = new();

    // Backward-compatibility aliases for Section 2 controls
    private NumericUpDown _snippetCountNumeric => _snippetsNumeric;
    private CheckBox _sceneChangeCheckBox => _useSceneChangeCheckBox;
    private NumericUpDown _percentNumeric => _startPercentNumeric;
    private NumericUpDown _fixedSecondsNumeric => _fixedSecNumeric;
    private RadioButton _percentOffsetRadio => _usePercentRadio;
    private RadioButton _fixedOffsetRadio => _useFixedSecRadio;
    private CheckBox _includeAudioCheckBox => _audioCheckBox;
    private CheckBox _smoothTransitionsCheckBox => _fadeCheckBox;

    // Section 3 controls
    private readonly TextBox _filterTextBox = new();
    private readonly ComboBox _statusFilterComboBox = new();
    private readonly Button _clearFilterButton = new();
    private readonly Button _checkMissingButton = new();
    private readonly Button _checkAllButton = new();
    private readonly Button _uncheckAllButton = new();
    private readonly Button _resetToAutoButton = new();
    private readonly DataGridView _videoGrid = new();
    private DataGridView _videosGrid => _videoGrid; // Backward-compatibility alias
    private int _lastCheckedRowIndex = -1;

    // Backward-compatibility aliases for Section 3 buttons
    private Button _selectMissingButton => _checkMissingButton;
    private Button _selectAllButton => _checkAllButton;
    private Button _selectNoneButton => _uncheckAllButton;

    // Section 4 & 5 controls
    private readonly ProgressBar _progressBar = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _logTextBox = new();
    private readonly Button _generateButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _scanButton = new();

    private bool _isGenerating;
    public bool IsGenerating => _isGenerating;

    private CancellationTokenSource? _cts;

    public MainForm()
    {
        Text = "Jellyfin Local Trailer Generator (*name_of_video*-trailer.mp4)";
        Width = 1180;
        Height = 820;
        MinimumSize = new Size(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5F);

        InitializeLayout();
        _ffmpegPathTextBox.Text = FfmpegGeneratorService.FindDefaultFfmpegPath();

        LoadSavedState();
    }

    private void InitializeLayout()
    {
        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5
        };
        // Row heights: Folders+FFmpeg (160px), Mode & Settings (155px), Video Grid (fill), Action Bar (48px), Log Output (fill)
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160)); // 1. Folders + FFmpeg path
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 155)); // 2. Generation Mode & Settings
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 64));   // 3. Video DataGridView
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));  // 4. Action bar + Progress
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 36));   // 5. Log output

        // ---------------------------------------------------------
        // 1. Top Panel: Media Folders & FFmpeg Path
        // ---------------------------------------------------------
        var topSplit = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        topSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        topSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));

        var foldersGroup = new GroupBox
        {
            Text = "1. Target Media Folders (Trailers are saved in the same folder as each video)",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var foldersLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        foldersLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foldersLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));

        _folderListBox.Dock = DockStyle.Fill;
        _folderListBox.IntegralHeight = false;

        var folderButtonsTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(6, 0, 0, 0)
        };
        folderButtonsTable.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
        folderButtonsTable.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
        folderButtonsTable.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34F));

        _addFolderButton.Text = "Add Folder...";
        _addFolderButton.Dock = DockStyle.Fill;
        _addFolderButton.Margin = new Padding(0, 0, 0, 4);
        _addFolderButton.Click += OnAddFolderClick;

        _removeFolderButton.Text = "Remove Selected";
        _removeFolderButton.Dock = DockStyle.Fill;
        _removeFolderButton.Margin = new Padding(0, 2, 0, 4);
        _removeFolderButton.Click += OnRemoveFolderClick;

        _scanButton.Text = "Scan Videos";
        _scanButton.Dock = DockStyle.Fill;
        _scanButton.Margin = new Padding(0, 2, 0, 0);
        _scanButton.Font = new Font(Font, FontStyle.Bold);
        _scanButton.BackColor = Color.FromArgb(230, 242, 252);
        _scanButton.Click += (_, _) => ScanSelectedFolders();

        folderButtonsTable.Controls.Add(_addFolderButton, 0, 0);
        folderButtonsTable.Controls.Add(_removeFolderButton, 0, 1);
        folderButtonsTable.Controls.Add(_scanButton, 0, 2);

        foldersLayout.Controls.Add(_folderListBox, 0, 0);
        foldersLayout.Controls.Add(folderButtonsTable, 1, 0);
        foldersGroup.Controls.Add(foldersLayout);

        var ffmpegGroup = new GroupBox
        {
            Text = "FFmpeg Executable (Jellyfin's ffmpeg.exe or system FFmpeg)",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var ffmpegLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2
        };
        ffmpegLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ffmpegLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        _ffmpegPathTextBox.Dock = DockStyle.Fill;
        var browseFfmpegBtn = new Button { Text = "Browse...", Width = 88, Height = 28 };
        browseFfmpegBtn.Click += OnBrowseFfmpegClick;

        var hintLabel = new Label
        {
            Text = "Naming Rule: [VideoFileNameWithoutExtension]-trailer.mp4\nSaved directly in the same folder as the original video.",
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            AutoSize = true
        };
        ffmpegLayout.Controls.Add(_ffmpegPathTextBox, 0, 0);
        ffmpegLayout.Controls.Add(browseFfmpegBtn, 1, 0);
        ffmpegLayout.Controls.Add(hintLabel, 0, 1);
        ffmpegLayout.SetColumnSpan(hintLabel, 2);
        ffmpegGroup.Controls.Add(ffmpegLayout);

        topSplit.Controls.Add(foldersGroup, 0, 0);
        topSplit.Controls.Add(ffmpegGroup, 1, 0);

        // ---------------------------------------------------------
        // 2. Settings Group (3 Trailer Generation Modes & Settings)
        // ---------------------------------------------------------
        var settingsGroup = new GroupBox
        {
            Text = "2. Trailer Generation Mode & Settings",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };

        var settingsTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        settingsTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        settingsTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Row 1 of Settings: Mode Selector + Description
        var modeFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoSize = false
        };

        _modeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeComboBox.Width = 530;
        _modeComboBox.Font = new Font(Font, FontStyle.Bold);
        _modeComboBox.Items.AddRange(
        [
            "1) RANDOM (Split into scenes & pick random moment per scene)",
            "2) SOUND DETECT (Split into scenes & center loudest moment)",
            "3) MOVEMENT DETECTION (Split into scenes & center most movement)"
        ]);
        _modeComboBox.SelectedIndex = 0;
        _modeComboBox.SelectedIndexChanged += (_, _) =>
        {
            UpdateModeControlsState();
            SaveCurrentState();
        };

        _modeDescriptionLabel.AutoSize = true;
        _modeDescriptionLabel.ForeColor = Color.FromArgb(40, 90, 150);
        _modeDescriptionLabel.Margin = new Padding(10, 6, 3, 3);

        modeFlow.Controls.AddRange(
        [
            new Label { Text = "Generation Mode:", AutoSize = true, Margin = new Padding(3, 6, 4, 3) },
            _modeComboBox,
            _modeDescriptionLabel
        ]);

        // Row 2 of Settings: Duration, Offsets, Parallel Jobs, Resolution, Audio, Transitions, Overwrite
        var optionsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            WrapContents = true
        };

        _durationNumeric.Minimum = 5;
        _durationNumeric.Maximum = 300;
        _durationNumeric.Value = 60;
        _durationNumeric.Width = 55;
        _durationNumeric.ValueChanged += (_, _) => SaveCurrentState();

        _snippetsNumeric.Minimum = 1;
        _snippetsNumeric.Maximum = 30;
        _snippetsNumeric.Value = 6;
        _snippetsNumeric.Width = 50;
        _snippetsNumeric.ValueChanged += (_, _) => SaveCurrentState();

        _useSceneChangeCheckBox.Text = "Use Scene Change";
        _useSceneChangeCheckBox.Checked = false;
        _useSceneChangeCheckBox.AutoSize = true;
        _useSceneChangeCheckBox.Margin = new Padding(4, 5, 4, 3);
        _useSceneChangeCheckBox.CheckedChanged += (_, _) =>
        {
            UpdateModeControlsState();
            SaveCurrentState();
        };

        _usePercentRadio.Text = "Start Skip %:";
        _usePercentRadio.Checked = true;
        _usePercentRadio.AutoSize = true;
        _usePercentRadio.CheckedChanged += (_, _) =>
        {
            UpdateOffsetControlsState();
            SaveCurrentState();
        };

        _startPercentNumeric.Minimum = 0;
        _startPercentNumeric.Maximum = 45;
        _startPercentNumeric.Value = 10;
        _startPercentNumeric.Width = 50;
        _startPercentNumeric.ValueChanged += (_, _) => SaveCurrentState();

        _useFixedSecRadio.Text = "Start Skip (s):";
        _useFixedSecRadio.AutoSize = true;
        _useFixedSecRadio.CheckedChanged += (_, _) =>
        {
            UpdateOffsetControlsState();
            SaveCurrentState();
        };

        _fixedSecNumeric.Minimum = 0;
        _fixedSecNumeric.Maximum = 7200;
        _fixedSecNumeric.Increment = 10;
        _fixedSecNumeric.Value = 120;
        _fixedSecNumeric.Width = 62;
        _fixedSecNumeric.ValueChanged += (_, _) => SaveCurrentState();

        var endPercentLabel = new Label
        {
            Text = "End Skip %:",
            AutoSize = true,
            Margin = new Padding(6, 6, 2, 3)
        };

        _endPercentNumeric.Minimum = 0;
        _endPercentNumeric.Maximum = 45;
        _endPercentNumeric.Value = 10;
        _endPercentNumeric.Width = 50;
        _endPercentNumeric.ValueChanged += (_, _) => SaveCurrentState();

        var parallelLabel = new Label
        {
            Text = "Parallel Jobs:",
            AutoSize = true,
            Margin = new Padding(4, 6, 2, 3)
        };

        _parallelCountNumeric.Minimum = 1;
        _parallelCountNumeric.Maximum = 16;
        _parallelCountNumeric.Value = 2;
        _parallelCountNumeric.Width = 48;
        _parallelCountNumeric.ValueChanged += (_, _) => SaveCurrentState();

        _resolutionComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _resolutionComboBox.Width = 115;
        _resolutionComboBox.Items.AddRange(["720p (Fast)", "480p (Compact)", "1080p (HD)", "Original"]);
        _resolutionComboBox.SelectedIndex = 0;
        _resolutionComboBox.SelectedIndexChanged += (_, _) => SaveCurrentState();

        _audioCheckBox.Text = "Include Audio";
        _audioCheckBox.Checked = true;
        _audioCheckBox.AutoSize = true;
        _audioCheckBox.CheckedChanged += (_, _) => SaveCurrentState();

        _fadeCheckBox.Text = "Smooth Fade Transitions";
        _fadeCheckBox.Checked = true;
        _fadeCheckBox.AutoSize = true;
        _fadeCheckBox.CheckedChanged += (_, _) => SaveCurrentState();

        _overwriteCheckBox.Text = "Overwrite existing *-trailer.mp4";
        _overwriteCheckBox.Checked = false;
        _overwriteCheckBox.AutoSize = true;
        _overwriteCheckBox.CheckedChanged += (_, _) =>
        {
            UpdateSelectionForOverwriteToggle();
            SaveCurrentState();
        };

        _overrideManualCheckBox.Text = "Override Manual (Use Auto)";
        _overrideManualCheckBox.Checked = false;
        _overrideManualCheckBox.AutoSize = true;
        _overrideManualCheckBox.CheckedChanged += (_, _) => SaveCurrentState();

        optionsFlow.Controls.AddRange(
        [
            new Label { Text = "Total Length (s):", AutoSize = true, Margin = new Padding(3, 6, 2, 3) },
            _durationNumeric,
            new Label { Text = "Segments across video:", AutoSize = true, Margin = new Padding(6, 6, 2, 3) },
            _snippetsNumeric,
            _useSceneChangeCheckBox,
            new Label { Text = " | ", AutoSize = true, Margin = new Padding(4, 6, 4, 3) },
            _usePercentRadio,
            _startPercentNumeric,
            _useFixedSecRadio,
            _fixedSecNumeric,
            endPercentLabel,
            _endPercentNumeric,
            new Label { Text = " | ", AutoSize = true, Margin = new Padding(4, 6, 4, 3) },
            parallelLabel,
            _parallelCountNumeric,
            new Label { Text = " | ", AutoSize = true, Margin = new Padding(4, 6, 4, 3) },
            new Label { Text = "Resolution:", AutoSize = true, Margin = new Padding(3, 6, 2, 3) },
            _resolutionComboBox,
            _audioCheckBox,
            _fadeCheckBox,
            _overwriteCheckBox,
            _overrideManualCheckBox
        ]);

        settingsTable.Controls.Add(modeFlow, 0, 0);
        settingsTable.Controls.Add(optionsFlow, 0, 1);
        settingsGroup.Controls.Add(settingsTable);

        // ---------------------------------------------------------
        // 3. Videos Grid Group (Filter & Sort Controls)
        // ---------------------------------------------------------
        var gridGroup = new GroupBox
        {
            Text = "3. Video List & Trailer Status",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var gridLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        gridLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        gridLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var filterSortBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false
        };

        _filterTextBox.Width = 210;
        _filterTextBox.PlaceholderText = "Search movie, folder, or status...";
        _filterTextBox.TextChanged += (_, _) => ApplyFilterAndSort();

        _statusFilterComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _statusFilterComboBox.Width = 160;
        _statusFilterComboBox.Items.AddRange(
        [
            "All Videos",
            "Missing Trailer",
            "Generated by App",
            "Not Generated by App",
            "Has Any Trailer",
            "Checked Only",
            "Manual Mode Only",
            "Automatic Mode Only"
        ]);
        _statusFilterComboBox.SelectedIndex = 0;
        _statusFilterComboBox.SelectedIndexChanged += (_, _) => ApplyFilterAndSort();

        _clearFilterButton.Text = "Clear";
        _clearFilterButton.Width = 55;
        _clearFilterButton.Height = 27;
        _clearFilterButton.Margin = new Padding(4, 3, 6, 3);
        _clearFilterButton.Click += (_, _) =>
        {
            _filterTextBox.Text = string.Empty;
            _statusFilterComboBox.SelectedIndex = 0;
        };

        _checkMissingButton.Text = "Check Missing Only";
        _checkMissingButton.Width = 140;
        _checkMissingButton.Height = 27;
        _checkMissingButton.Margin = new Padding(2, 3, 4, 3);
        _checkMissingButton.Click += (_, _) =>
        {
            if (_isGenerating) return;
            var targetList = string.IsNullOrEmpty(_filterTextBox.Text.Trim()) && _statusFilterComboBox.SelectedIndex == 0
                ? _allVideos
                : _videos.ToList();

            foreach (var v in targetList)
            {
                v.IsSelected = !v.HasTrailer && !v.WasGenerated;
            }
            _videoGrid.Refresh();
            SaveCurrentState();
        };

        _checkAllButton.Text = "Check All";
        _checkAllButton.Width = 80;
        _checkAllButton.Height = 27;
        _checkAllButton.Margin = new Padding(2, 3, 4, 3);
        _checkAllButton.Click += (_, _) =>
        {
            if (_isGenerating) return;
            var targetList = string.IsNullOrEmpty(_filterTextBox.Text.Trim()) && _statusFilterComboBox.SelectedIndex == 0
                ? _allVideos
                : _videos.ToList();

            foreach (var v in targetList)
            {
                v.IsSelected = true;
            }
            _videoGrid.Refresh();
            SaveCurrentState();
        };

        _uncheckAllButton.Text = "Uncheck All";
        _uncheckAllButton.Width = 90;
        _uncheckAllButton.Height = 27;
        _uncheckAllButton.Margin = new Padding(2, 3, 4, 3);
        _uncheckAllButton.Click += (_, _) =>
        {
            if (_isGenerating) return;
            var targetList = string.IsNullOrEmpty(_filterTextBox.Text.Trim()) && _statusFilterComboBox.SelectedIndex == 0
                ? _allVideos
                : _videos.ToList();

            foreach (var v in targetList)
            {
                v.IsSelected = false;
            }
            _videoGrid.Refresh();
            SaveCurrentState();
        };

        _resetToAutoButton.Text = "Reset Manual -> Auto";
        _resetToAutoButton.Width = 135;
        _resetToAutoButton.Height = 27;
        _resetToAutoButton.Margin = new Padding(2, 3, 4, 3);
        _resetToAutoButton.Click += OnResetToAutoButtonClick;

        filterSortBar.Controls.AddRange(
        [
            new Label { Text = "Filter:", AutoSize = true, Margin = new Padding(3, 8, 2, 3) },
            _filterTextBox,
            new Label { Text = "Status:", AutoSize = true, Margin = new Padding(8, 8, 2, 3) },
            _statusFilterComboBox,
            _clearFilterButton,
            new Label { Text = " | ", AutoSize = true, Margin = new Padding(2, 8, 6, 3) },
            _checkMissingButton,
            _checkAllButton,
            _uncheckAllButton,
            _resetToAutoButton
        ]);

        ConfigureDataGridView();
        gridLayout.Controls.Add(filterSortBar, 0, 0);
        gridLayout.Controls.Add(_videoGrid, 0, 1);
        gridGroup.Controls.Add(gridLayout);

        // ---------------------------------------------------------
        // 4. Action Bar & Progress
        // ---------------------------------------------------------
        var actionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1
        };
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));

        _generateButton.Text = "Generate Trailers";
        _generateButton.Dock = DockStyle.Fill;
        _generateButton.Font = new Font(Font, FontStyle.Bold);
        _generateButton.BackColor = Color.FromArgb(0, 120, 212);
        _generateButton.ForeColor = Color.White;
        _generateButton.FlatStyle = FlatStyle.Flat;
        _generateButton.Click += GenerateButton_Click;

        _cancelButton.Text = "Cancel";
        _cancelButton.Dock = DockStyle.Fill;
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => _cts?.Cancel();

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Margin = new Padding(8, 10, 8, 10);

        _statusLabel.Text = "Ready. Add a folder and click 'Scan Videos'.";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleRight;

        actionPanel.Controls.Add(_generateButton, 0, 0);
        actionPanel.Controls.Add(_cancelButton, 1, 0);
        actionPanel.Controls.Add(_progressBar, 2, 0);
        actionPanel.Controls.Add(_statusLabel, 3, 0);

        // ---------------------------------------------------------
        // 5. Activity & FFmpeg Log
        // ---------------------------------------------------------
        var logGroup = new GroupBox
        {
            Text = "Activity & FFmpeg Log",
            Dock = DockStyle.Fill,
            Padding = new Padding(6)
        };
        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ReadOnly = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.BackColor = Color.FromArgb(24, 24, 28);
        _logTextBox.ForeColor = Color.FromArgb(220, 225, 230);
        _logTextBox.Font = new Font("Consolas", 9F);
        logGroup.Controls.Add(_logTextBox);

        rootLayout.Controls.Add(topSplit, 0, 0);
        rootLayout.Controls.Add(settingsGroup, 0, 1);
        rootLayout.Controls.Add(gridGroup, 0, 2);
        rootLayout.Controls.Add(actionPanel, 0, 3);
        rootLayout.Controls.Add(logGroup, 0, 4);

        Controls.Add(rootLayout);
    }

    private void UpdateModeControlsState()
    {
        var selectedMode = (TrailerGenerationMode)_modeComboBox.SelectedIndex;

        UpdateOffsetControlsState();

        _snippetsNumeric.Enabled = !_isGenerating && !_useSceneChangeCheckBox.Checked;
        _fadeCheckBox.Enabled = true;

        string splitDesc = _useSceneChangeCheckBox.Checked
            ? "Splits usable video by scene changes"
            : "Splits usable video into fixed scenes";

        _modeDescriptionLabel.Text = selectedMode switch
        {
            TrailerGenerationMode.Random =>
                $"{splitDesc} and picks a random moment from each scene.",
            TrailerGenerationMode.SoundDetect =>
                $"{splitDesc} and centers the loudest moment in each scene sequence.",
            TrailerGenerationMode.MovementDetection =>
                $"{splitDesc} and centers the moment with most movement in each scene sequence.",
            _ => string.Empty
        };
    }

    private void UpdateOffsetControlsState()
    {
        _startPercentNumeric.Enabled = _usePercentRadio.Checked;
        _fixedSecNumeric.Enabled = _useFixedSecRadio.Checked;
        _endPercentNumeric.Enabled = true;
    }

    private void ConfigureDataGridView()
    {
        _videoGrid.Dock = DockStyle.Fill;
        _videoGrid.AutoGenerateColumns = false;
        _videoGrid.AllowUserToAddRows = false;
        _videoGrid.AllowUserToDeleteRows = false;
        _videoGrid.RowHeadersVisible = false;
        _videoGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _videoGrid.MultiSelect = true;
        _videoGrid.BackgroundColor = Color.White;

        var colSelect = new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(VideoEntry.IsSelected),
            HeaderText = "Generate",
            Width = 70,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        var colFileName = new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(VideoEntry.FileName),
            HeaderText = "Source Video",
            Width = 240,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        var colTrailer = new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(VideoEntry.TrailerFileName),
            HeaderText = "Target Trailer File (Same Folder)",
            Width = 260,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        var colMode = new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(VideoEntry.GenerationType),
            HeaderText = "Mode",
            Width = 100,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        var colStatus = new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(VideoEntry.StatusMessage),
            HeaderText = "Status",
            Width = 170,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        var colFolder = new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(VideoEntry.FolderPath),
            HeaderText = "Folder Path",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic
        };

        _videoGrid.Columns.AddRange([colSelect, colFileName, colTrailer, colMode, colStatus, colFolder]);

        _videoGrid.Sorted += (_, _) =>
        {
            if (_videoGrid.SortedColumn != null)
            {
                _lastSortPropertyName = _videoGrid.SortedColumn.DataPropertyName;
                _lastSortDirection = _videoGrid.SortOrder == SortOrder.Descending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            }
        };

        _videoGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_videoGrid.IsCurrentCellDirty)
            {
                _videoGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        _videoGrid.CellValueChanged += (_, e) =>
        {
            if (!_isLoadingState && e.RowIndex >= 0 && e.ColumnIndex == 0)
            {
                SaveCurrentState();
            }
        };

        _videoGrid.CellMouseDown += OnVideoGridCellMouseDown;
        _videoGrid.CellContentClick += OnVideoGridCellContentClick;
        _videoGrid.CellDoubleClick += OnVideoGridCellDoubleClick;
        _videoGrid.KeyDown += OnVideoGridKeyDown;
    }

    private void ApplyFilterAndSort()
    {
        _lastCheckedRowIndex = -1;

        if (_videoGrid.SortedColumn != null)
        {
            _lastSortPropertyName = _videoGrid.SortedColumn.DataPropertyName;
            _lastSortDirection = _videoGrid.SortOrder == SortOrder.Descending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }

        var filterText = _filterTextBox.Text.Trim();
        var statusIndex = _statusFilterComboBox.SelectedIndex;

        IEnumerable<VideoEntry> query = _allVideos;

        if (!string.IsNullOrEmpty(filterText))
        {
            query = query.Where(v =>
                v.FileName.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                v.FolderPath.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                v.TrailerFileName.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                v.GenerationType.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                v.StatusMessage.Contains(filterText, StringComparison.OrdinalIgnoreCase));
        }

        query = statusIndex switch
        {
            1 => query.Where(v => !v.HasTrailer),
            2 => query.Where(v => v.WasGenerated),
            3 => query.Where(v => !v.WasGenerated),
            4 => query.Where(v => v.HasTrailer || v.WasGenerated),
            5 => query.Where(v => v.IsSelected),
            6 => query.Where(v => v.IsManual),
            7 => query.Where(v => !v.IsManual),
            _ => query // 0 = All Videos
        };

        var filteredList = query.ToList();
        _videos = new SortableBindingList<VideoEntry>(filteredList);

        if (!string.IsNullOrEmpty(_lastSortPropertyName))
        {
            var prop = TypeDescriptor.GetProperties(typeof(VideoEntry))[_lastSortPropertyName];
            if (prop != null)
            {
                ((IBindingList)_videos).ApplySort(prop, _lastSortDirection);
            }
        }

        _videoGrid.DataSource = _videos;

        if (!string.IsNullOrEmpty(_lastSortPropertyName))
        {
            var col = _videoGrid.Columns.Cast<DataGridViewColumn>()
                .FirstOrDefault(c => c.DataPropertyName == _lastSortPropertyName);
            if (col != null)
            {
                col.HeaderCell.SortGlyphDirection = _lastSortDirection == ListSortDirection.Ascending
                    ? SortOrder.Ascending
                    : SortOrder.Descending;
            }
        }
    }

    private void LoadSavedState()
    {
        _isLoadingState = true;
        try
        {
            var state = _stateService.LoadState();
            if (state != null)
            {
                // Restore folders
                _folderListBox.Items.Clear();
                if (state.Folders != null)
                {
                    foreach (var folder in state.Folders)
                    {
                        if (!string.IsNullOrWhiteSpace(folder))
                        {
                            _folderListBox.Items.Add(folder);
                        }
                    }
                }

                // Restore Section 2 settings
                if (state.GenerationMode >= 0 && state.GenerationMode < _modeComboBox.Items.Count)
                {
                    _modeComboBox.SelectedIndex = state.GenerationMode;
                }
                else
                {
                    _modeComboBox.SelectedIndex = 0;
                }

                _usePercentRadio.Checked = state.UseStartPercentage;
                _useFixedSecRadio.Checked = !state.UseStartPercentage;

                _startPercentNumeric.Value = Math.Clamp((decimal)state.StartPercentage, _startPercentNumeric.Minimum, _startPercentNumeric.Maximum);
                _fixedSecNumeric.Value = Math.Clamp((decimal)state.FixedStartSeconds, _fixedSecNumeric.Minimum, _fixedSecNumeric.Maximum);
                _endPercentNumeric.Value = Math.Clamp((decimal)state.EndSkipPercentage, _endPercentNumeric.Minimum, _endPercentNumeric.Maximum);
                _durationNumeric.Value = Math.Clamp(state.TargetDurationSeconds, (int)_durationNumeric.Minimum, (int)_durationNumeric.Maximum);
                _snippetsNumeric.Value = Math.Clamp(state.SnippetCount, (int)_snippetsNumeric.Minimum, (int)_snippetsNumeric.Maximum);
                _parallelCountNumeric.Value = Math.Clamp(state.MaxParallelCount, (int)_parallelCountNumeric.Minimum, (int)_parallelCountNumeric.Maximum);

                var resIndex = GetResolutionIndex(state.ResolutionScale);
                if (resIndex >= 0 && resIndex < _resolutionComboBox.Items.Count)
                {
                    _resolutionComboBox.SelectedIndex = resIndex;
                }
                else
                {
                    _resolutionComboBox.SelectedIndex = 0;
                }

                _audioCheckBox.Checked = state.IncludeAudio;
                _fadeCheckBox.Checked = state.SmoothFadeTransitions;
                _overwriteCheckBox.Checked = state.OverwriteExisting;
                _useSceneChangeCheckBox.Checked = state.UseSceneChangeDetection;
                _overrideManualCheckBox.Checked = state.OverrideManualWithAuto;

                // Restore videos
                _allVideos.Clear();
                if (state.Videos != null)
                {
                    foreach (var saved in state.Videos)
                    {
                        if (string.IsNullOrWhiteSpace(saved.VideoPath))
                        {
                            continue;
                        }

                        var entry = new VideoEntry
                        {
                            VideoPath = saved.VideoPath
                        };
                        bool diskExists = File.Exists(entry.TrailerPath);
                        entry.HasTrailer = diskExists;
                        entry.WasGenerated = saved.WasGenerated;
                        entry.IsSelected = saved.IsSelected;
                        entry.IsManual = saved.IsManual && saved.ManualSegments is { Count: > 0 };
                        entry.ManualSegments = saved.ManualSegments?.Select(s => s.Clone()).ToList() ?? new List<ManualSegmentItem>();
                        entry.CachedAnalysis = AppStatePersistenceService.ToVideoTimelineAnalysisResult(saved.CachedAnalysis);
                        entry.SavedConfigurations = AppStatePersistenceService.ToConfigurationsDictionary(saved.SavedConfigurations);

                        if (entry.WasGenerated && diskExists)
                        {
                            entry.StatusMessage = "Generated ✓";
                        }
                        else if (entry.IsManual && !diskExists && !entry.WasGenerated)
                        {
                            entry.StatusMessage = "Manual Configured";
                        }
                        else if (!string.IsNullOrWhiteSpace(saved.StatusMessage))
                        {
                            entry.StatusMessage = saved.StatusMessage;
                        }
                        else
                        {
                            entry.StatusMessage = diskExists ? "Exists (Skipped)" : "Missing Trailer";
                        }

                        _allVideos.Add(entry);
                    }
                }
            }
            else
            {
                // Default settings if no state file exists
                _modeComboBox.SelectedIndex = 0;
                _usePercentRadio.Checked = true;
                _useFixedSecRadio.Checked = false;
                _startPercentNumeric.Value = 10;
                _fixedSecNumeric.Value = 120;
                _endPercentNumeric.Value = 10;
                _durationNumeric.Value = 60;
                _snippetsNumeric.Value = 6;
                _parallelCountNumeric.Value = 2;
                _resolutionComboBox.SelectedIndex = 0;
                _audioCheckBox.Checked = true;
                _fadeCheckBox.Checked = true;
                _overwriteCheckBox.Checked = false;
                _useSceneChangeCheckBox.Checked = false;
                _overrideManualCheckBox.Checked = false;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to load saved state: {ex.Message}");
        }
        finally
        {
            _isLoadingState = false;
        }

        UpdateModeControlsState();
        UpdateOffsetControlsState();
        ApplyFilterAndSort();

        if (_allVideos.Count > 0)
        {
            var missingCount = _allVideos.Count(x => !x.HasTrailer && !x.WasGenerated);
            _statusLabel.Text = $"Restored {_allVideos.Count} videos ({missingCount} missing trailers).";
        }
    }

    private void SaveCurrentState()
    {
        if (_isLoadingState)
        {
            return;
        }

        try
        {
            var state = new AppStateModel
            {
                Folders = _folderListBox.Items.Cast<string>().ToList(),
                Videos = _allVideos.Select(v => new SavedVideoEntry
                {
                    VideoPath = v.VideoPath,
                    IsSelected = v.IsSelected,
                    WasGenerated = v.WasGenerated,
                    StatusMessage = v.StatusMessage,
                    IsManual = v.IsManual,
                    ManualSegments = v.ManualSegments.Select(s => s.Clone()).ToList(),
                    CachedAnalysis = AppStatePersistenceService.ToSavedTimelineAnalysis(v.CachedAnalysis),
                    SavedConfigurations = AppStatePersistenceService.ToSavedConfigurations(v.SavedConfigurations)
                }).ToList(),
                GenerationMode = _modeComboBox.SelectedIndex,
                UseStartPercentage = _usePercentRadio.Checked,
                StartPercentage = (double)_startPercentNumeric.Value,
                FixedStartSeconds = (double)_fixedSecNumeric.Value,
                EndSkipPercentage = (double)_endPercentNumeric.Value,
                TargetDurationSeconds = (int)_durationNumeric.Value,
                SnippetCount = (int)_snippetsNumeric.Value,
                MaxParallelCount = (int)_parallelCountNumeric.Value,
                ResolutionScale = GetSelectedResolutionScale(),
                IncludeAudio = _audioCheckBox.Checked,
                SmoothFadeTransitions = _fadeCheckBox.Checked,
                OverwriteExisting = _overwriteCheckBox.Checked,
                UseSceneChangeDetection = _useSceneChangeCheckBox.Checked,
                OverrideManualWithAuto = _overrideManualCheckBox.Checked
            };

            _stateService.SaveState(state);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to save app state: {ex.Message}");
        }
    }

    private static int GetResolutionIndex(string? resolutionScale)
    {
        if (string.IsNullOrWhiteSpace(resolutionScale))
        {
            return 0;
        }

        if (resolutionScale.Contains("1280") || resolutionScale.Contains("720"))
        {
            return 0;
        }

        if (resolutionScale.Contains("854") || resolutionScale.Contains("480"))
        {
            return 1;
        }

        if (resolutionScale.Contains("1920") || resolutionScale.Contains("1080"))
        {
            return 2;
        }

        if (resolutionScale.Equals("Original", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return 0;
    }

    private string GetSelectedResolutionScale()
    {
        return _resolutionComboBox.SelectedIndex switch
        {
            0 => "1280:-2",
            1 => "854:-2",
            2 => "1920:-2",
            3 => "Original",
            _ => "1280:-2"
        };
    }

    private void OnAddFolderClick(object? sender, EventArgs e)
    {
        if (_isGenerating)
        {
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a media folder containing movies/videos",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            var existing = _folderListBox.Items.Cast<string>()
                .Any(p => string.Equals(p, dialog.SelectedPath, StringComparison.OrdinalIgnoreCase));
            if (!existing)
            {
                _folderListBox.Items.Add(dialog.SelectedPath);
                ScanSelectedFolders();
            }
        }
    }

    private void OnRemoveFolderClick(object? sender, EventArgs e)
    {
        if (_isGenerating)
        {
            return;
        }

        if (_folderListBox.SelectedIndex >= 0)
        {
            _folderListBox.Items.RemoveAt(_folderListBox.SelectedIndex);
            ScanSelectedFolders();
        }
    }

    private void OnBrowseFfmpegClick(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select ffmpeg.exe",
            Filter = "FFmpeg Executable (ffmpeg.exe)|ffmpeg.exe|All Executables (*.exe)|*.exe"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _ffmpegPathTextBox.Text = dialog.FileName;
        }
    }

    private void ScanSelectedFolders()
    {
        if (_isGenerating)
        {
            return;
        }

        var folders = _folderListBox.Items.Cast<string>().ToList();

        if (folders.Count == 0)
        {
            _allVideos.Clear();
            ApplyFilterAndSort();
            _statusLabel.Text = "No folders selected.";
            SaveCurrentState();
            return;
        }

        // Build lookup of previous videos to preserve WasGenerated from prior runs
        var previousLookup = _allVideos
            .Where(v => !string.IsNullOrEmpty(v.VideoPath))
            .ToDictionary(v => v.VideoPath, StringComparer.OrdinalIgnoreCase);

        var scanned = _scanner.ScanFolders(folders);
        var newAllVideos = new List<VideoEntry>();

        foreach (var item in scanned)
        {
            if (previousLookup.TryGetValue(item.VideoPath, out var prev))
            {
                item.CachedAnalysis = prev.CachedAnalysis;
                item.SavedConfigurations = new Dictionary<string, List<ManualSegmentItem>>(prev.SavedConfigurations ?? new(), StringComparer.OrdinalIgnoreCase);
                item.IsManual = prev.IsManual;
                item.ManualSegments = prev.ManualSegments.Select(s => s.Clone()).ToList();
                if (item.IsManual && !item.HasTrailer && !item.WasGenerated)
                {
                    item.StatusMessage = "Manual Configured";
                }

                if (prev.WasGenerated && item.HasTrailer)
                {
                    item.WasGenerated = true;
                    if (!_overwriteCheckBox.Checked)
                    {
                        item.StatusMessage = "Generated ✓";
                    }
                }

                if (!_overwriteCheckBox.Checked)
                {
                    item.IsSelected = prev.IsSelected;
                }
            }

            if (_overwriteCheckBox.Checked)
            {
                item.IsSelected = true;
                if (item.HasTrailer)
                {
                    item.StatusMessage = "Will Overwrite";
                }
            }

            newAllVideos.Add(item);
        }

        _allVideos = newAllVideos;
        ApplyFilterAndSort();

        var missingCount = _allVideos.Count(x => !x.HasTrailer && !x.WasGenerated);
        _statusLabel.Text = $"Found {_allVideos.Count} videos ({missingCount} missing trailers).";
        AppendLog($"Scanned {folders.Count} folder(s): {_allVideos.Count} videos found, {missingCount} missing *-trailer.mp4.");

        SaveCurrentState();
    }

    private void UpdateSelectionForOverwriteToggle()
    {
        foreach (var entry in _allVideos)
        {
            if (entry.HasTrailer)
            {
                entry.IsSelected = _overwriteCheckBox.Checked;
                entry.StatusMessage = _overwriteCheckBox.Checked ? "Will Overwrite" : "Exists (Skipped)";
            }
        }
        _videoGrid.Refresh();
        SaveCurrentState();
    }

    private void GenerateButton_Click(object? sender, EventArgs e) => OnGenerateClick(sender, e);

    private async void OnGenerateClick(object? sender, EventArgs e)
    {
        await GenerateTrailersAsync();
    }

    /// <summary>
    /// Executes parallel multi-process trailer generation across selected videos.
    /// Thread-safe: all model mutations and state saves happen on the UI thread.
    /// </summary>
    public async Task GenerateTrailersAsync(CancellationToken cancellationToken = default)
    {
        if (_isGenerating)
        {
            return;
        }

        _videoGrid.EndEdit();
        var toProcess = _allVideos.Where(v => v.IsSelected).ToList();
        if (toProcess.Count == 0)
        {
            MessageBox.Show(this, "No videos are selected for trailer generation.", "Nothing Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ffmpegPath = _ffmpegPathTextBox.Text.Trim();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            MessageBox.Show(this, "Please specify the path to ffmpeg.exe.", "FFmpeg Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var options = BuildCurrentGenerationOptions();

        SetBusyState(true);
        _progressBar.Minimum = 0;
        _progressBar.Maximum = toProcess.Count;
        _progressBar.Value = 0;

        _cts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();

        int activeJobs = 0;
        int completedCount = 0;
        int successCount = 0;
        int failCount = 0;
        int totalCount = toProcess.Count;

        var parallelDegree = Math.Max(1, options.MaxConcurrentProcesses);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelDegree,
            CancellationToken = _cts.Token
        };

        try
        {
            AppendLog($"Starting trailer generation for {totalCount} video(s) ({parallelDegree} concurrent job(s), Mode: {options.Mode})...");

            await Parallel.ForEachAsync(toProcess, parallelOptions, async (item, token) =>
            {
                token.ThrowIfCancellationRequested();

                var active = Interlocked.Increment(ref activeJobs);
                var done = Volatile.Read(ref completedCount);

                RunOnUiThread(() =>
                {
                    item.StatusMessage = "Generating...";
                    _statusLabel.Text = $"[Active: {active} | Done: {done}/{totalCount}] {item.FileName}: Generating...";
                    _videoGrid.Refresh();
                });

                try
                {
                    var itemOptions = CloneOptions(options);
                    if (item.IsManual && item.ManualSegments.Count > 0 && !_overrideManualCheckBox.Checked)
                    {
                        itemOptions.ManualSegments = item.ManualSegments.Select(s => s.Clone()).ToList();
                    }
                    else
                    {
                        itemOptions.ManualSegments = null;
                    }

                    await _generator.GenerateTrailerAsync(
                        item.VideoPath,
                        item.TrailerPath,
                        itemOptions,
                        AppendLog,
                        token,
                        item);

                    var diskExists = File.Exists(item.TrailerPath);
                    Interlocked.Increment(ref successCount);
                    var currentDone = Interlocked.Increment(ref completedCount);

                    RunOnUiThread(() =>
                    {
                        item.HasTrailer = diskExists;
                        item.WasGenerated = true;
                        item.StatusMessage = "Done ✓";
                        item.IsSelected = false;
                        _progressBar.Value = Math.Min(_progressBar.Maximum, currentDone);
                        _statusLabel.Text = $"[Active: {Volatile.Read(ref activeJobs)} | Done: {currentDone}/{totalCount}] {item.FileName}: Done ✓";
                        _videoGrid.Refresh();
                        SaveCurrentState();
                    });
                }
                catch (OperationCanceledException)
                {
                    RunOnUiThread(() =>
                    {
                        item.StatusMessage = "Cancelled";
                        _videoGrid.Refresh();
                    });
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failCount);
                    var currentDone = Interlocked.Increment(ref completedCount);

                    RunOnUiThread(() =>
                    {
                        item.StatusMessage = "Failed";
                        _progressBar.Value = Math.Min(_progressBar.Maximum, currentDone);
                        _statusLabel.Text = $"[Active: {Volatile.Read(ref activeJobs)} | Done: {currentDone}/{totalCount}] {item.FileName}: Failed";
                        _videoGrid.Refresh();
                        SaveCurrentState();
                    });

                    AppendLog($"ERROR [{item.FileName}]: {ex.Message}");
                }
                finally
                {
                    Interlocked.Decrement(ref activeJobs);
                }
            });

            _statusLabel.Text = $"Finished: {successCount} generated, {failCount} failed ({totalCount} total).";
            AppendLog($"=== Batch complete: {successCount} generated, {failCount} failed. ===");
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = $"Cancelled: {successCount} completed, {failCount} failed.";
            AppendLog("=== Batch generation cancelled by user. ===");
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Any(e => e is OperationCanceledException))
        {
            _statusLabel.Text = $"Cancelled: {successCount} completed, {failCount} failed.";
            AppendLog("=== Batch generation cancelled by user. ===");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            RunOnUiThread(() =>
            {
                SetBusyState(false);
                _videoGrid.Refresh();
                SaveCurrentState();
            });
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isGenerating)
        {
            _cts?.Cancel();
        }
        base.OnFormClosing(e);
        SaveCurrentState();
    }

    private void SetBusyState(bool busy)
    {
        _isGenerating = busy;
        _generateButton.Enabled = !busy;
        _cancelButton.Enabled = busy;
        _scanButton.Enabled = !busy;
        _addFolderButton.Enabled = !busy;
        _removeFolderButton.Enabled = !busy;
        _checkMissingButton.Enabled = !busy;
        _checkAllButton.Enabled = !busy;
        _uncheckAllButton.Enabled = !busy;
        _parallelCountNumeric.Enabled = !busy;
        _modeComboBox.Enabled = !busy;
        _useSceneChangeCheckBox.Enabled = !busy;
        _snippetsNumeric.Enabled = !busy && !_useSceneChangeCheckBox.Checked;
        _resetToAutoButton.Enabled = !busy;
        _overrideManualCheckBox.Enabled = !busy;
    }

    /// <summary>
    /// Builds the trailer generation options model from current Section 2 UI controls.
    /// </summary>
    public TrailerGenerationOptions BuildCurrentGenerationOptions()
    {
        var maxHeight = _resolutionComboBox.SelectedIndex switch
        {
            0 => 720,
            1 => 480,
            2 => 1080,
            _ => 0
        };

        return new TrailerGenerationOptions
        {
            FfmpegPath = _ffmpegPathTextBox.Text.Trim(),
            Mode = (TrailerGenerationMode)_modeComboBox.SelectedIndex,
            ClipDurationSeconds = (int)_durationNumeric.Value,
            TargetDurationSeconds = (int)_durationNumeric.Value,
            UsePercentageOffset = _usePercentRadio.Checked,
            StartPercentage = (double)_startPercentNumeric.Value,
            FixedStartSeconds = (double)_fixedSecNumeric.Value,
            EndSkipPercentage = (double)_endPercentNumeric.Value,
            SnippetCount = (int)_snippetsNumeric.Value,
            MaxConcurrentProcesses = (int)_parallelCountNumeric.Value,
            ResolutionScale = GetSelectedResolutionScale(),
            MaxHeightResolution = maxHeight,
            IncludeAudio = _audioCheckBox.Checked,
            SmoothFadeTransitions = _fadeCheckBox.Checked,
            SmoothSceneTransitions = _fadeCheckBox.Checked,
            OverwriteExisting = _overwriteCheckBox.Checked,
            UseSceneChangeDetection = _useSceneChangeCheckBox.Checked
        };
    }

    private static TrailerGenerationOptions CloneOptions(TrailerGenerationOptions source)
    {
        return new TrailerGenerationOptions
        {
            FfmpegPath = source.FfmpegPath,
            Mode = source.Mode,
            ClipDurationSeconds = source.ClipDurationSeconds,
            TargetDurationSeconds = source.TargetDurationSeconds,
            UsePercentageOffset = source.UsePercentageOffset,
            StartPercentage = source.StartPercentage,
            FixedStartSeconds = source.FixedStartSeconds,
            EndSkipPercentage = source.EndSkipPercentage,
            SnippetCount = source.SnippetCount,
            MaxConcurrentProcesses = source.MaxConcurrentProcesses,
            ResolutionScale = source.ResolutionScale,
            MaxHeightResolution = source.MaxHeightResolution,
            IncludeAudio = source.IncludeAudio,
            SmoothFadeTransitions = source.SmoothFadeTransitions,
            SmoothSceneTransitions = source.SmoothSceneTransitions,
            OverwriteExisting = source.OverwriteExisting,
            UseSceneChangeDetection = source.UseSceneChangeDetection
        };
    }

    private void OnVideoGridCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (_isGenerating || e.RowIndex < 0 || e.RowIndex >= _videos.Count || e.ColumnIndex == 0)
        {
            return;
        }

        var entry = _videos[e.RowIndex];
        var baseOptions = BuildCurrentGenerationOptions();

        using var editor = new ManualTrailerEditorForm(entry, _generator, baseOptions);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            entry.NotifyStatusChanged();
            _videoGrid.Refresh();

            if (editor.SavedAsManual)
            {
                AppendLog($"Saved manual trailer configuration for '{entry.FileName}' ({entry.ManualSegments.Count} segments).");
            }
            else if (editor.ResetToAutomaticRequested)
            {
                AppendLog($"Reset '{entry.FileName}' to Automatic trailer mode.");
            }

            SaveCurrentState();
        }
    }

    private void OnResetToAutoButtonClick(object? sender, EventArgs e)
    {
        if (_isGenerating)
        {
            return;
        }

        var selectedFromGrid = _videoGrid.SelectedRows
            .Cast<DataGridViewRow>()
            .Where(r => r.Index >= 0 && r.Index < _videos.Count)
            .Select(r => _videos[r.Index])
            .ToHashSet();

        var targetedManuals = _videos
            .Where(v => (v.IsSelected || selectedFromGrid.Contains(v)) && v.IsManual)
            .ToList();

        var listToReset = targetedManuals.Count > 0
            ? targetedManuals
            : _videos.Where(v => v.IsManual).ToList();

        if (listToReset.Count == 0)
        {
            return;
        }

        foreach (var v in listToReset)
        {
            v.IsManual = false;
            v.ManualSegments.Clear();
            v.StatusMessage = v.HasTrailer ? "Exists (Skipped)" : "Missing Trailer";
            v.NotifyStatusChanged();
        }

        _videoGrid.Refresh();
        SaveCurrentState();
        AppendLog($"Reset {listToReset.Count} video(s) to Automatic mode.");
    }

    private void OnVideoGridCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (_isGenerating || e.ColumnIndex != 0 || e.RowIndex < 0 || e.RowIndex >= _videos.Count || e.Button != MouseButtons.Left)
        {
            return;
        }

        if ((ModifierKeys & Keys.Shift) == Keys.Shift && _lastCheckedRowIndex >= 0 && _lastCheckedRowIndex < _videos.Count)
        {
            bool targetState = !_videos[e.RowIndex].IsSelected;
            _videoGrid.CancelEdit();
            int start = Math.Min(_lastCheckedRowIndex, e.RowIndex);
            int end = Math.Max(_lastCheckedRowIndex, e.RowIndex);
            for (int i = start; i <= end; i++)
            {
                _videos[i].IsSelected = targetState;
            }
            _lastCheckedRowIndex = e.RowIndex;
            _videoGrid.RefreshEdit();
            _videoGrid.Refresh();
            SaveCurrentState();
        }
        else
        {
            if (_videoGrid.SelectedRows.Count > 1 && _videoGrid.Rows[e.RowIndex].Selected)
            {
                bool targetState = !_videos[e.RowIndex].IsSelected;
                _videoGrid.CancelEdit();
                foreach (DataGridViewRow row in _videoGrid.SelectedRows)
                {
                    if (row.Index >= 0 && row.Index < _videos.Count)
                    {
                        _videos[row.Index].IsSelected = targetState;
                    }
                }
                _videoGrid.RefreshEdit();
                _videoGrid.Refresh();
                SaveCurrentState();
            }
            _lastCheckedRowIndex = e.RowIndex;
        }
    }

    private void OnVideoGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex == 0 && e.RowIndex >= 0 && e.RowIndex < _videos.Count)
        {
            if ((ModifierKeys & Keys.Shift) == Keys.Shift || (_videoGrid.SelectedRows.Count > 1 && _videoGrid.Rows[e.RowIndex].Selected))
            {
                _videoGrid.CancelEdit();
            }
        }
    }

    private void OnVideoGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && !_isGenerating && _videoGrid.SelectedRows.Count > 0)
        {
            var firstRow = _videoGrid.SelectedRows[0];
            bool targetState = true;
            if (firstRow.Index >= 0 && firstRow.Index < _videos.Count)
            {
                targetState = !_videos[firstRow.Index].IsSelected;
            }

            foreach (DataGridViewRow row in _videoGrid.SelectedRows)
            {
                if (row.Index >= 0 && row.Index < _videos.Count)
                {
                    _videos[row.Index].IsSelected = targetState;
                }
            }

            _videoGrid.RefreshEdit();
            _videoGrid.Refresh();
            SaveCurrentState();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Safely executes an action on the WinForms UI thread, preventing cross-thread exceptions.
    /// </summary>
    private void RunOnUiThread(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (!IsHandleCreated)
        {
            action();
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (Exception) when (IsDisposed || Disposing)
        {
            // Ignore dispatch errors during form teardown
        }
    }

    private void AppendLog(string message)
    {
        RunOnUiThread(() =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            _logTextBox.AppendText(line);
        });
    }
}
