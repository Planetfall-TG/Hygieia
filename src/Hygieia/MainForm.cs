using System.Text.Json;
using AppText = Hygieia.Text;

namespace Hygieia;

sealed class MainForm : Form
{
    const int DisplayLimit = 5000;
    const string Footer =
        "Unused-space scrubbing overwrites clusters the filesystem marks free, which is where deleted " +
        "file contents usually remain. It does not send a drive sanitize command, so solid-state wear " +
        "leveling and volume shadow copies can still hold data. The scan lists names still stored in " +
        "the Recycle Bin or the NTFS file table. Scrubbing free space does not rewrite that table.";

    readonly ComboBox _algorithms = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label _summary = new();
    readonly Label _note = new();
    readonly Button _delete = new();
    readonly Button _free = new();
    readonly ComboBox _drives = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox _margin = new() { Text = "Leave a safety margin so Windows keeps working", Checked = true };
    readonly Label _driveDetail = new();
    readonly ListView _found = new();
    readonly TextBox _filter = new();
    readonly Label _foundLabel = new() { Text = "No scan yet." };
    readonly Button _scan = new();
    readonly Button _recycle = new();
    readonly Button _admin = new();
    readonly Button _refresh = new();
    readonly Button _cancel = new();
    readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1000 };
    readonly RichTextBox _log = new();
    readonly Label _status = new() { Text = "Ready", AutoSize = true };
    readonly Label _progressText = new() { Text = "Idle", AutoSize = true };

    readonly List<DiskVolume> _volumes = new();
    List<DeletedEntry> _entries = new();
    List<DeletedEntry> _shown = new();
    string _sortColumn = "path";
    bool _sortReverse;
    bool _busy;
    bool _closing;
    bool _loadingAlgorithm;
    CancellationTokenSource? _cancelSource;
    Thread? _worker;

    public bool IsClosing => _closing;

    public MainForm()
    {
        Text = "Hygieia";
        Theme.ApplyIcon(this);
        BackColor = Theme.Paper;
        Font = Theme.Ui(10);
        MinimumSize = new Size(900, 760);
        var screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        Width = Math.Min(1180, Math.Max(960, screen.Width - 80));
        Height = Math.Min(980, Math.Max(760, screen.Height - 80));
        StartPosition = FormStartPosition.CenterScreen;
        HandleCreated += (_, _) => Theme.DarkTitle(this);

        var footer = new Label
        {
            Text = Footer,
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 78,
            ForeColor = Theme.Muted,
            BackColor = Theme.Paper,
            Padding = new Padding(22, 6, 22, 10),
            Font = Theme.Ui(8),
        };
        var header = BuildHeader();
        var body = BuildBody();
        Controls.Add(body);
        Controls.Add(header);
        Controls.Add(footer);
        Resize += (_, _) =>
        {
            var wrap = Math.Max(420, ClientSize.Width - 80);
            _summary.MaximumSize = new Size(wrap, 0);
            _note.MaximumSize = new Size(wrap, 0);
            footer.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 44), 0);
        };
        var initialWrap = Math.Max(420, Width - 120);
        _summary.MaximumSize = new Size(initialWrap, 0);
        _note.MaximumSize = new Size(initialWrap, 0);
        LoadDrives();
        Shown += (_, _) => Task.Run(CleanupStale);
        FormClosing += OnFormClosing;
    }

    Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = Theme.Paper, Padding = new Padding(22, 12, 22, 8) };
        var bar = new Panel { Width = 6, Dock = DockStyle.Left, BackColor = Theme.Pine, Margin = new Padding(0, 0, 12, 0) };
        var titles = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Paper, Padding = new Padding(12, 0, 0, 0) };
        var title = new Label
        {
            Text = "Hygieia",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = Theme.Display(22),
            Padding = new Padding(0, 0, 0, 2),
            ForeColor = Theme.Ink,
            BackColor = Theme.Paper,
        };
        var tag = new Label
        {
            Text = "Overwrite files, folders, and the unused space left by deleted data.",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Theme.Muted,
            BackColor = Theme.Paper,
        };
        _status.Dock = DockStyle.Right;
        _status.ForeColor = Theme.Muted;
        _status.BackColor = Theme.Paper;
        _status.TextAlign = ContentAlignment.MiddleRight;
        titles.Controls.Add(tag);
        titles.Controls.Add(title);
        header.Controls.Add(titles);
        header.Controls.Add(bar);
        header.Controls.Add(_status);
        return header;
    }

    Control BuildBody()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(22, 8, 22, 8),
            BackColor = Theme.Paper,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var algo = new Card { Dock = DockStyle.Fill, AutoSize = true };
        _algorithms.Dock = DockStyle.Top;
        _algorithms.Font = Theme.Ui(10);
        Theme.StyleCombo(_algorithms);
        foreach (var algorithm in Algorithms.All)
            _algorithms.Items.Add(algorithm.Name);
        _loadingAlgorithm = true;
        var saved = LoadAlgorithmName();
        var index = Math.Max(0, _algorithms.Items.IndexOf(saved));
        _algorithms.SelectedIndex = index;
        _loadingAlgorithm = false;
        _algorithms.SelectedIndexChanged += (_, _) => OnAlgorithm();
        _summary.Dock = DockStyle.Top;
        _summary.AutoSize = true;
        _summary.ForeColor = Theme.Ink;
        _summary.BackColor = Theme.White;
        _summary.Padding = new Padding(0, 6, 0, 0);
        _note.Dock = DockStyle.Top;
        _note.AutoSize = true;
        _note.ForeColor = Theme.Muted;
        _note.BackColor = Theme.White;
        _note.Font = Theme.Ui(9);
        _note.Padding = new Padding(0, 2, 0, 0);
        algo.Controls.Add(_note);
        algo.Controls.Add(_summary);
        algo.Controls.Add(_algorithms);
        algo.Controls.Add(Section("ALGORITHM"));
        ShowAlgorithm();
        body.Controls.Add(algo, 0, 0);
        body.SetColumnSpan(algo, 2);

        var actions = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 68),
            Margin = new Padding(0, 0, 0, 8),
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        StyleAction(_delete, "Delete…\nFiles and folders", Theme.Danger, Theme.DangerDark, Color.White);
        StyleAction(_free, "Scrub unused disk space\nWhere deleted data used to be", Theme.Pine, Theme.PineDark, Theme.OnAccent);
        _delete.Click += (_, _) => OnDelete();
        _free.Click += (_, _) => OnScrubUnused();
        _delete.Margin = new Padding(0, 0, 6, 0);
        _free.Margin = new Padding(6, 0, 0, 0);
        actions.Controls.Add(_delete, 0, 0);
        actions.Controls.Add(_free, 1, 0);
        body.Controls.Add(actions, 0, 1);
        body.SetColumnSpan(actions, 2);

        var drive = new Card { Dock = DockStyle.Fill, AutoSize = true };
        var driveHead = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.White, WrapContents = false };
        driveHead.Controls.Add(Section("DRIVE"));
        driveHead.Controls.Add(new Label
        {
            Text = "Used by unused-space scrubbing and the deleted-file search",
            AutoSize = true,
            ForeColor = Theme.Muted,
            BackColor = Theme.White,
            Font = Theme.Ui(9),
            Margin = new Padding(8, 6, 0, 0),
        });
        var driveRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, Height = 36, BackColor = Theme.White };
        driveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        driveRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _drives.Dock = DockStyle.Fill;
        Theme.StyleCombo(_drives);
        _drives.SelectedIndexChanged += (_, _) => UpdateDriveDetail();
        _refresh.Text = "Refresh";
        StyleQuiet(_refresh);
        _refresh.Click += (_, _) => LoadDrives();
        driveRow.Controls.Add(_drives, 0, 0);
        driveRow.Controls.Add(_refresh, 1, 0);
        _margin.Dock = DockStyle.Top;
        _margin.AutoSize = true;
        _margin.BackColor = Theme.White;
        _margin.ForeColor = Theme.Ink;
        Theme.StyleCheck(_margin);
        _margin.CheckedChanged += (_, _) => UpdateDriveDetail();
        _driveDetail.Dock = DockStyle.Top;
        _driveDetail.AutoSize = true;
        _driveDetail.ForeColor = Theme.Muted;
        _driveDetail.BackColor = Theme.White;
        _driveDetail.Font = Theme.Ui(9);
        drive.Controls.Add(_driveDetail);
        drive.Controls.Add(_margin);
        drive.Controls.Add(driveRow);
        drive.Controls.Add(driveHead);
        body.Controls.Add(drive, 0, 2);
        body.SetColumnSpan(drive, 2);

        body.Controls.Add(BuildDeleted(), 0, 3);
        body.Controls.Add(BuildActivity(), 1, 3);
        return body;
    }

    Control BuildDeleted()
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 6, 0) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.White };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Section("PREVIOUSLY DELETED"), 0, 0);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Theme.White, WrapContents = true };
        _scan.Text = "Find deleted files";
        _recycle.Text = "Scrub Recycle Bin copies";
        _admin.Text = Native.IsAdmin() ? "Running as administrator" : "Scan as administrator";
        _admin.Enabled = !Native.IsAdmin();
        foreach (var button in new[] { _scan, _recycle, _admin })
        {
            StyleQuiet(button);
            tools.Controls.Add(button);
        }
        _scan.Click += (_, _) => OnScan();
        _recycle.Click += (_, _) => OnScrubRecycle();
        _admin.Click += (_, _) => OnElevate();
        layout.Controls.Add(tools, 0, 1);
        var filterRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Height = 28, BackColor = Theme.White };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterRow.Controls.Add(new Label { Text = "Filter", AutoSize = true, ForeColor = Theme.Muted, BackColor = Theme.White, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        _filter.Dock = DockStyle.Fill;
        Theme.StyleField(_filter);
        _filter.TextChanged += (_, _) => ApplyFilter();
        filterRow.Controls.Add(_filter, 1, 0);
        layout.Controls.Add(filterRow, 0, 2);
        _foundLabel.AutoSize = true;
        _foundLabel.ForeColor = Theme.Muted;
        _foundLabel.BackColor = Theme.White;
        _foundLabel.Font = Theme.Ui(9);
        layout.Controls.Add(_foundLabel, 0, 3);
        _found.Dock = DockStyle.Fill;
        _found.View = View.Details;
        _found.FullRowSelect = true;
        _found.GridLines = false;
        Theme.StyleList(_found);
        _found.Columns.Add("Name", 120);
        _found.Columns.Add("Path", 180);
        _found.Columns.Add("Kind", 56);
        _found.Columns.Add("Size", 68);
        _found.Columns.Add("Source", 96);
        _found.Columns.Add("Deleted", 104);
        _found.Columns.Add("Note", 150);
        _found.ColumnClick += (_, e) => SortBy(_found.Columns[e.Column].Text.ToLowerInvariant());
        _found.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                e.SuppressKeyPress = true;
                CopyPaths();
            }
        };
        layout.Controls.Add(_found, 0, 4);
        card.Controls.Add(layout);
        return card;
    }

    Control BuildActivity()
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(6, 0, 0, 0) };
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 2, BackColor = Theme.White };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _progressText.ForeColor = Theme.Ink;
        _progressText.BackColor = Theme.White;
        _progressText.Dock = DockStyle.Fill;
        _cancel.Text = "Cancel";
        StyleQuiet(_cancel);
        _cancel.Enabled = false;
        _cancel.Click += (_, _) => OnCancel();
        top.Controls.Add(_progressText, 0, 0);
        top.Controls.Add(_cancel, 1, 0);
        _progress.Dock = DockStyle.Top;
        _progress.Height = 18;
        _progress.Margin = new Padding(0, 6, 0, 6);
        Theme.StyleProgress(_progress);
        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        Theme.StyleField(_log);
        _log.BackColor = Theme.Field;
        _log.Font = Theme.Ui(9);
        _log.DetectUrls = false;
        var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), BackColor = Theme.White };
        wrap.Controls.Add(_log);
        card.Controls.Add(wrap);
        card.Controls.Add(_progress);
        card.Controls.Add(top);
        return card;
    }

    static Label Section(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Theme.Ui(8, FontStyle.Bold),
        ForeColor = Theme.Label,
        BackColor = Theme.White,
        Dock = DockStyle.Top,
        Padding = new Padding(0, 0, 0, 4),
    };

    static void StyleAction(Button button, string text, Color back, Color down, Color fore)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Height = 64;
        button.MinimumSize = new Size(0, 64);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = back;
        button.ForeColor = fore;
        button.Font = Theme.Ui(11);
        button.Cursor = Cursors.Hand;
        button.MouseDown += (_, _) => button.BackColor = down;
        button.MouseUp += (_, _) => { if (button.Enabled) button.BackColor = back; };
        button.Tag = back;
    }

    static void StyleQuiet(Button button)
    {
        button.AutoSize = true;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Theme.White;
        button.ForeColor = Theme.Ink;
        button.Cursor = Cursors.Hand;
        button.Margin = new Padding(0, 0, 6, 0);
        button.Padding = new Padding(8, 4, 8, 4);
        button.FlatAppearance.BorderColor = Theme.Line;
        button.MouseEnter += (_, _) => { if (button.Enabled) button.FlatAppearance.BorderColor = Theme.Pine; };
        button.MouseLeave += (_, _) => button.FlatAppearance.BorderColor = Theme.Line;
    }

    void ShowAlgorithm()
    {
        var algorithm = CurrentAlgorithm();
        _summary.Text = algorithm.Summary;
        _note.Text = algorithm.Note;
    }

    void OnAlgorithm()
    {
        ShowAlgorithm();
        if (!_loadingAlgorithm)
            SaveAlgorithmName(CurrentAlgorithm().Name);
    }

    Algorithm CurrentAlgorithm()
    {
        var name = _algorithms.SelectedItem as string;
        return name != null && Algorithms.ByName.TryGetValue(name, out var algorithm) ? algorithm : Algorithms.Default;
    }

    void LoadDrives()
    {
        if (_busy)
            return;
        var previous = CurrentDrive()?.Root;
        try
        {
            var (drives, network) = Native.ListDrives();
            _volumes.Clear();
            _volumes.AddRange(drives);
            FillDrives(previous);
            foreach (var root in network)
            {
                var captured = root;
                Task.Run(() =>
                {
                    DiskVolume? drive = null;
                    try { drive = Native.Probe(captured); }
                    catch (Exception) { drive = null; }
                    BeginInvoke(() => ReplaceDrive(captured, drive));
                });
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not list drives. {AppText.Friendly(ex)}", error: true);
        }
    }

    void FillDrives(string? prefer)
    {
        _drives.Items.Clear();
        foreach (var drive in _volumes)
            _drives.Items.Add(drive.MenuText());
        var system = (Path.GetPathRoot(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows") ?? "C:").ToUpperInvariant();
        var index = 0;
        for (var i = 0; i < _volumes.Count; i++)
        {
            if (prefer != null && _volumes[i].Root.Equals(prefer, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
            if (prefer == null && _volumes[i].Letter.Equals(system, StringComparison.OrdinalIgnoreCase))
                index = i;
        }
        if (_volumes.Count > 0)
            _drives.SelectedIndex = index;
        UpdateDriveDetail();
    }

    void ReplaceDrive(string root, DiskVolume? drive)
    {
        var replacement = drive ?? new DiskVolume(root, "Unavailable", "Network", "", 0, 0, true);
        for (var i = 0; i < _volumes.Count; i++)
        {
            if (_volumes[i].Root.Equals(root, StringComparison.OrdinalIgnoreCase))
                _volumes[i] = replacement;
        }
        FillDrives(CurrentDrive()?.Root ?? root);
    }

    DiskVolume? CurrentDrive()
    {
        var index = _drives.SelectedIndex;
        return index < 0 || index >= _volumes.Count ? null : _volumes[index];
    }

    void UpdateDriveDetail()
    {
        var drive = CurrentDrive();
        var ready = drive is { Ready: true } && !(drive.Label == "Unavailable" && drive.Total == 0);
        _free.Enabled = !_busy && ready;
        if (drive == null)
        {
            _driveDetail.Text = "No drive is available.";
            return;
        }
        if (!drive.Ready)
        {
            _driveDetail.Text = $"{drive.Kind} · checking free space";
            return;
        }
        if (drive.Label == "Unavailable" && drive.Total == 0)
        {
            _driveDetail.Text = $"{drive.Kind} · this drive did not respond";
            return;
        }
        var reserve = Native.ReserveBytes(drive.Root, _margin.Checked);
        var writable = Math.Max(0, drive.Free - reserve);
        var text = drive.Kind;
        if (drive.FileSystem.Length > 0)
            text += $" · {drive.FileSystem}";
        if (drive.Label.Length > 0)
            text += $" · {drive.Label}";
        text += $" · {AppText.FormatBytes(drive.Free)} free · {AppText.FormatBytes(writable)} will be overwritten";
        if (drive.Kind == "Network")
            text += " · this writes to the remote computer";
        _driveDetail.Text = text;
    }

    void CleanupStale()
    {
        try
        {
            var removed = Native.CleanupStaleWorkspaces();
            if (removed.Count > 0 && IsHandleCreated && !IsDisposed)
                BeginInvoke(() => AppendLog("Removed leftover wipe files from an earlier run."));
        }
        catch (Exception)
        {
            // A locked leftover can wait for the next launch.
        }
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _delete.Enabled = !busy;
        _scan.Enabled = !busy;
        _recycle.Enabled = !busy;
        _refresh.Enabled = !busy;
        _drives.Enabled = !busy;
        _algorithms.Enabled = !busy;
        _margin.Enabled = !busy;
        _cancel.Enabled = busy;
        if (busy)
        {
            _delete.BackColor = Theme.Disabled;
            _delete.ForeColor = Theme.DisabledText;
            _free.BackColor = Theme.Disabled;
            _free.ForeColor = Theme.DisabledText;
            _free.Enabled = false;
        }
        else
        {
            _delete.BackColor = Theme.Danger;
            _delete.ForeColor = Color.White;
            _free.BackColor = Theme.Pine;
            _free.ForeColor = Theme.OnAccent;
            UpdateDriveDetail();
        }
        _status.Text = busy ? "Working…" : "Ready";
    }

    void OnCancel()
    {
        _cancelSource?.Cancel();
        _status.Text = "Stopping…";
        AppendLog("Stopping…");
    }

    void Start(Action<IReporter> work, Action completed)
    {
        if (_worker is { IsAlive: true })
            return;
        _cancelSource = new CancellationTokenSource();
        var reporter = new UiReporter(this, _cancelSource);
        SetBusy(true);
        _worker = new Thread(() =>
        {
            try
            {
                work(reporter);
            }
            catch (CancelledException)
            {
                reporter.Log("Cancelled.");
            }
            catch (Exception ex)
            {
                reporter.Log($"Stopped. {AppText.Friendly(ex)}", error: true);
            }
            finally
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(completed);
            }
        })
        { IsBackground = false, Name = "hygieia" };
        _worker.Start();
    }

    void OnDelete()
    {
        if (_busy)
            return;
        var picked = PickerForm.Ask(this);
        if (picked == null || picked.Count == 0)
            return;
        var paths = PathRules.PruneNested(picked);
        var blocked = paths.Select(path => (path, reason: PathRules.ProtectedReason(path))).Where(item => item.reason != null).ToList();
        if (blocked.Count > 0)
        {
            MessageBox.Show(this, "Nothing was scrubbed.\n\n" + string.Join("\n\n", blocked.Select(item => $"{item.path}\n{item.reason}")), "Hygieia", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _progress.Style = ProgressBarStyle.Marquee;
        _progressText.Text = "Counting…";
        Start(reporter =>
        {
            var plan = Scrubber.Walk(paths, reporter);
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => ContinueScrub(plan, "Scrub files and folders", false));
        }, () => { });
    }

    void ContinueScrub(Plan plan, string title, bool recycle)
    {
        _progress.Style = ProgressBarStyle.Continuous;
        if (_closing)
        {
            Close();
            return;
        }
        if (!plan.HasWork)
        {
            foreach (var (path, reason) in plan.Skipped)
                AppendLog($"Skipped {path}. {reason}");
            if (plan.Skipped.Count == 0)
                AppendLog("Nothing to scrub.");
            SetBusy(false);
            _progressText.Text = "Idle";
            return;
        }
        var paragraphs = new List<string>
        {
            $"Scrub {plan.CountText()} ({AppText.FormatBytes(plan.TotalBytes)}) with {CurrentAlgorithm().Name}?",
            recycle
                ? "Recycle Bin copies are overwritten and removed. They cannot be restored."
                : "This overwrites the data, then deletes the items. They do not go to the Recycle Bin and cannot be restored.",
        };
        if (!recycle && plan.Skipped.Count > 0)
            paragraphs.Add($"{plan.Skipped.Count:N0} links or unreadable items will be skipped.");
        if (!ConfirmForm.Ask(this, title, paragraphs, plan.Roots))
        {
            AppendLog("Scrub cancelled.");
            SetBusy(false);
            _progressText.Text = "Idle";
            return;
        }
        var algorithm = CurrentAlgorithm();
        _progress.Value = 0;
        Start(reporter => Scrubber.ScrubPlan(plan, algorithm, reporter), Idle);
    }

    void OnScrubUnused()
    {
        if (_busy)
            return;
        var drive = CurrentDrive();
        if (drive == null)
        {
            MessageBox.Show(this, "Choose a drive first.", "Hygieia", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (!drive.Ready)
        {
            MessageBox.Show(this, "Still checking free space on that drive.", "Hygieia", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (drive.Label == "Unavailable" && drive.Total == 0)
        {
            MessageBox.Show(this, "That drive did not respond.", "Hygieia", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var reserve = Native.ReserveBytes(drive.Root, _margin.Checked);
        var writable = Math.Max(0, drive.Free - reserve);
        if (writable <= 0)
        {
            MessageBox.Show(this, $"{drive.Letter} has {AppText.FormatBytes(drive.Free)} free. Hygieia leaves {AppText.FormatBytes(reserve)} free, so there is nothing to scrub.", "Hygieia", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var algorithm = CurrentAlgorithm();
        var paragraphs = new List<string>
        {
            $"Scrub unused space on {drive.Letter} with {algorithm.Name}?",
            $"About {AppText.FormatBytes(writable)} will be overwritten. {AppText.FormatBytes(reserve)} stays free.",
            "Temporary files are written on this drive and removed at the end.",
        };
        if (drive.Kind == "Network")
            paragraphs.Add("This is a network drive. The write goes to the remote computer.");
        if (algorithm.PassCount >= 7)
            paragraphs.Add($"This runs {algorithm.PassCount} passes and can take a long time.");
        if (!ConfirmForm.Ask(this, "Scrub unused disk space", paragraphs, new[] { drive.Root }))
        {
            AppendLog("Unused-space scrub cancelled.");
            return;
        }
        var root = drive.Root;
        Start(reporter => Scrubber.ScrubUnused(root, algorithm, reporter, reserve), Idle);
    }

    void OnScan()
    {
        if (_busy)
            return;
        var drive = CurrentDrive();
        if (drive == null)
        {
            MessageBox.Show(this, "Choose a drive first.", "Hygieia", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var root = drive.Root;
        AppendLog($"Looking for deleted names on {root}.");
        Start(reporter =>
        {
            var result = DeletedFiles.ScanDrive(root, reporter);
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => ShowScan(result));
        }, Idle);
    }

    void OnElevate()
    {
        if (Native.IsAdmin())
            return;
        if (Native.RelaunchElevated())
        {
            AppendLog("Opening an administrator window. Approve the Windows prompt.");
            var timer = new System.Windows.Forms.Timer { Interval = 400 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                Close();
            };
            timer.Start();
        }
        else
            AppendLog("Administrator approval was not granted. Recycle Bin results are still available.", error: true);
    }

    void OnScrubRecycle()
    {
        if (_busy)
            return;
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Filtered())
        {
            foreach (var path in entry.ScrubPaths)
            {
                var full = Path.GetFullPath(path);
                if (seen.Add(full))
                    paths.Add(full);
            }
        }
        if (paths.Count == 0)
        {
            MessageBox.Show(this, "None of the shown items are Recycle Bin copies.\nNames found only in the file table are erased by scrubbing unused disk space.", "Hygieia", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var blocked = paths.Select(path => (path, reason: PathRules.ProtectedReason(path))).Where(item => item.reason != null).ToList();
        if (blocked.Count > 0)
        {
            MessageBox.Show(this, "Nothing was scrubbed.\n\n" + string.Join("\n\n", blocked.Select(item => $"{item.path}\n{item.reason}")), "Hygieia", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _progress.Style = ProgressBarStyle.Marquee;
        Start(reporter =>
        {
            var plan = Scrubber.Walk(PathRules.PruneNested(paths), reporter);
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => ContinueScrub(plan, "Scrub Recycle Bin copies", true));
        }, () => { });
    }

    void ShowScan(ScanResult result)
    {
        _entries = result.Entries;
        foreach (var note in result.Notes)
            AppendLog(note, error: true);
        ApplyFilter();
    }

    List<DeletedEntry> Filtered()
    {
        var text = _filter.Text.Trim();
        IEnumerable<DeletedEntry> rows = _entries;
        if (text.Length > 0)
        {
            rows = _entries.Where(entry =>
                entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                entry.Path.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                entry.Note.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                entry.Source.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            var result = CompareEntry(a, b);
            return _sortReverse ? -result : result;
        });
        return list;
    }

    int CompareEntry(DeletedEntry a, DeletedEntry b)
    {
        if (_sortColumn == "size")
            return (a.Size ?? -1).CompareTo(b.Size ?? -1);
        string Left(DeletedEntry entry) => _sortColumn switch
        {
            "name" => entry.Name,
            "kind" => entry.Kind,
            "deleted" => entry.Deleted,
            "source" => entry.Source,
            "note" => entry.Note,
            _ => entry.Path,
        };
        return string.Compare(Left(a), Left(b), StringComparison.OrdinalIgnoreCase);
    }

    void SortBy(string column)
    {
        if (_sortColumn == column)
            _sortReverse = !_sortReverse;
        else
        {
            _sortColumn = column;
            _sortReverse = false;
        }
        ApplyFilter();
    }

    void ApplyFilter()
    {
        _shown = Filtered();
        _found.BeginUpdate();
        _found.Items.Clear();
        foreach (var entry in _shown.Take(DisplayLimit))
        {
            var size = "—";
            if (entry.Size != null && !(entry.Kind == "Folder" && entry.Size == 0))
                size = AppText.FormatBytes(entry.Size.Value);
            var item = new ListViewItem(entry.Name);
            item.SubItems.Add(entry.Path);
            item.SubItems.Add(entry.Kind);
            item.SubItems.Add(size);
            item.SubItems.Add(entry.Source);
            item.SubItems.Add(entry.Deleted);
            item.SubItems.Add(entry.Note);
            _found.Items.Add(item);
        }
        _found.EndUpdate();
        if (_entries.Count == 0)
        {
            _foundLabel.Text = "No deleted names found.";
            return;
        }
        var visible = Math.Min(_shown.Count, DisplayLimit);
        _foundLabel.Text = _shown.Count > DisplayLimit
            ? $"Showing {visible:N0} of {_shown.Count:N0} matching names. {_entries.Count:N0} found."
            : $"Showing {visible:N0} of {_entries.Count:N0} found.";
    }

    void CopyPaths()
    {
        var paths = new List<string>();
        foreach (int index in _found.SelectedIndices)
        {
            if (index >= 0 && index < _shown.Count)
                paths.Add(_shown[index].Path);
        }
        if (paths.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
            _status.Text = "Copied the path.";
        }
    }

    public void AppendLog(string message, bool error = false)
    {
        if (IsDisposed)
            return;
        _log.SelectionStart = _log.TextLength;
        _log.SelectionColor = error ? Theme.Danger : Theme.Ink;
        _log.AppendText(message + Environment.NewLine);
        var lines = _log.Lines;
        if (lines.Length > 400)
        {
            _log.Select(0, _log.GetFirstCharIndexFromLine(lines.Length - 300));
            _log.SelectedText = "";
        }
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    public void SetProgress(double fraction, string detail)
    {
        if (IsDisposed)
            return;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = Math.Max(0, Math.Min(1000, (int)(fraction * 1000)));
        _progressText.Text = detail;
    }

    void Idle()
    {
        SetBusy(false);
        _progress.Style = ProgressBarStyle.Continuous;
        if (!_closing)
        {
            _status.Text = "Ready";
            _progressText.Text = "Idle";
        }
        else
            Close();
    }

    void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_worker is { IsAlive: true })
        {
            e.Cancel = true;
            _closing = true;
            _cancelSource?.Cancel();
            _status.Text = "Stopping and removing temporary files…";
            var timer = new System.Windows.Forms.Timer { Interval = 200 };
            timer.Tick += (_, _) =>
            {
                if (_worker is { IsAlive: true })
                    return;
                timer.Stop();
                timer.Dispose();
                Close();
            };
            timer.Start();
        }
    }

    static string SettingsPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, "Hygieia", "settings.json");
    }

    static string LoadAlgorithmName()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath()));
            if (doc.RootElement.TryGetProperty("algorithm", out var name))
            {
                var text = name.GetString();
                if (text != null && Algorithms.ByName.ContainsKey(text))
                    return text;
            }
        }
        catch (Exception)
        {
            // A missing or broken settings file falls back to the default pattern.
        }
        return Algorithms.Default.Name;
    }

    static void SaveAlgorithmName(string name)
    {
        try
        {
            var path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string> { ["algorithm"] = name }));
        }
        catch (Exception)
        {
            // The chosen pattern still applies for this run.
        }
    }

    sealed class UiReporter : IReporter
    {
        readonly MainForm _form;
        readonly CancellationTokenSource _cancel;
        readonly Pulse _pulse = new(0.08);

        public UiReporter(MainForm form, CancellationTokenSource cancel)
        {
            _form = form;
            _cancel = cancel;
        }

        public bool IsCancelled => _cancel.IsCancellationRequested || _form.IsClosing;

        public void Log(string message, bool error = false)
        {
            if (_form.IsDisposed || !_form.IsHandleCreated)
                return;
            try { _form.BeginInvoke(() => _form.AppendLog(message, error)); }
            catch (Exception) { }
        }

        public void Progress(double fraction, string detail)
        {
            if (!_pulse.Ready(fraction >= 1) || _form.IsDisposed || !_form.IsHandleCreated)
                return;
            try { _form.BeginInvoke(() => _form.SetProgress(fraction, detail)); }
            catch (Exception) { }
        }
    }
}
