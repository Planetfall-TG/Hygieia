using AppText = Hygieia.Text;

namespace Hygieia;

sealed class PickerForm : Form
{
    readonly TextBox _address = new();
    readonly ListView _files = new();
    readonly ListBox _cart = new();
    readonly Label _status = new();
    readonly Button _select = new();
    readonly Button _addFolder = new();
    readonly List<string> _chosen = new();
    string? _current;

    PickerForm(string? start)
    {
        Text = "Select files and folders to scrub";
        BackColor = Theme.Paper;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Ui(10);
        StartPosition = FormStartPosition.CenterParent;
        HandleCreated += (_, _) => Theme.DarkTitle(this);
        MinimumSize = new Size(860, 680);
        ClientSize = new Size(960, 720);

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(16, 14, 16, 14),
            BackColor = Theme.Paper,
        };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));

        outer.Controls.Add(new Label
        {
            Text = "Select files and folders to scrub",
            AutoSize = true,
            Font = Theme.Ui(14, FontStyle.Bold),
            ForeColor = Theme.Ink,
            BackColor = Theme.Paper,
            Margin = new Padding(0, 0, 0, 2),
        }, 0, 0);
        var hint = new Label
        {
            Text = "Open a folder with a double-click. Add files and folders from more than one place, then select them.",
            AutoSize = true,
            MaximumSize = new Size(840, 0),
            ForeColor = Theme.Muted,
            BackColor = Theme.Paper,
            Margin = new Padding(0, 0, 0, 8),
        };
        outer.Controls.Add(hint, 0, 1);
        Resize += (_, _) => hint.MaximumSize = new Size(Math.Max(240, ClientSize.Width - 48), 0);

        var addressRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _address.Dock = DockStyle.Fill;
        Theme.StyleField(_address);
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                GoAddress();
            }
        };
        var up = SmallButton("Up", () => Up());
        addressRow.Controls.Add(_address, 0, 0);
        addressRow.Controls.Add(up, 1, 0);
        outer.Controls.Add(addressRow, 0, 2);

        _files.Dock = DockStyle.Fill;
        _files.View = View.Details;
        _files.FullRowSelect = true;
        _files.HideSelection = false;
        Theme.StyleList(_files);
        _files.Columns.Add("Name", 360);
        _files.Columns.Add("Type", 90);
        _files.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _files.Columns.Add("Modified", 150);
        _files.DoubleClick += (_, _) => OpenSelection();
        _files.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                OpenSelection();
            }
            else if (e.KeyCode == Keys.Back)
                Up();
            else if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem item in _files.Items)
                    item.Selected = true;
            }
        };
        outer.Controls.Add(_files, 0, 3);

        _status.AutoSize = true;
        _status.ForeColor = Theme.Muted;
        _status.BackColor = Theme.Paper;
        _status.Margin = new Padding(0, 6, 0, 4);
        outer.Controls.Add(_status, 0, 4);
        outer.Controls.Add(new Label
        {
            Text = "Selected",
            AutoSize = true,
            Font = Theme.Ui(9, FontStyle.Bold),
            ForeColor = Theme.Label,
            BackColor = Theme.Paper,
        }, 0, 5);
        _cart.Dock = DockStyle.Fill;
        Theme.StyleListBox(_cart);
        outer.Controls.Add(_cart, 0, 6);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            BackColor = Theme.Paper,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 8, 0, 0),
            Padding = new Padding(0, 4, 0, 0),
        };
        actions.Controls.Add(SmallButton("Add selected", AddSelected));
        _addFolder = SmallButton("Add this folder", AddCurrentFolder);
        actions.Controls.Add(_addFolder);
        actions.Controls.Add(SmallButton("Remove", RemoveSelected));
        var cancel = SmallButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); });
        _select.Text = "Select";
        _select.AutoSize = true;
        _select.Padding = new Padding(16, 6, 16, 6);
        _select.FlatStyle = FlatStyle.Flat;
        _select.FlatAppearance.BorderSize = 0;
        _select.Font = Theme.Ui(10, FontStyle.Bold);
        _select.Margin = new Padding(8, 0, 0, 0);
        _select.Click += (_, _) =>
        {
            if (_chosen.Count == 0)
                return;
            DialogResult = DialogResult.OK;
            Close();
        };
        var right = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Right,
            BackColor = Theme.Paper,
            Margin = new Padding(0, 8, 0, 0),
            Padding = new Padding(0, 4, 0, 0),
        };
        right.Controls.Add(_select);
        right.Controls.Add(cancel);
        var buttonRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            Dock = DockStyle.Fill,
            BackColor = Theme.Paper,
            Margin = new Padding(0),
        };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        buttonRow.Controls.Add(actions, 0, 0);
        buttonRow.Controls.Add(right, 1, 0);
        outer.RowCount = 8;
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.Controls.Add(buttonRow, 0, 7);

        Controls.Add(outer);
        CancelButton = cancel;
        Shown += (_, _) => BeginInvoke(() =>
        {
            Size = new Size(960, 720);
            hint.MaximumSize = new Size(Math.Max(240, ClientSize.Width - 48), 0);
        });
        RefreshSelect();
        ShowDir(start);
    }

    public IReadOnlyList<string> Chosen => _chosen;

    public static List<string>? Ask(IWin32Window owner)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var start = Directory.Exists(home) ? home : null;
        using var dialog = new PickerForm(start);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Chosen.ToList() : null;
    }

    void ShowDir(string? path)
    {
        _current = path;
        _files.Items.Clear();
        if (path == null)
        {
            _address.Text = "This PC";
            _addFolder.Enabled = false;
            ShowComputer();
            return;
        }
        _addFolder.Enabled = true;
        _address.Text = path;
        var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
        if (!string.IsNullOrEmpty(parent) || PathRules.IsDriveRoot(path))
            _files.Items.Add(new ListViewItem("..") { Tag = ".." });
        try
        {
            var entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
            }).ToList();
            var folders = new List<FileSystemInfo>();
            var files = new List<FileSystemInfo>();
            foreach (var entry in entries)
            {
                if (Native.IsReparse(entry.FullName) || (entry.Attributes & FileAttributes.Directory) != 0)
                    folders.Add(entry);
                else
                    files.Add(entry);
            }
            folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var entry in folders)
                AddEntry(entry, "Folder");
            foreach (var entry in files)
                AddEntry(entry, "File");
            _status.Text = $"{folders.Count:N0} folders, {files.Count:N0} files";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    void ShowComputer()
    {
        try
        {
            var (drives, _) = Native.ListDrives();
            foreach (var drive in drives)
            {
                var label = string.IsNullOrEmpty(drive.Label) ? drive.Kind : drive.Label;
                var item = new ListViewItem($"{drive.Letter}  {label}") { Tag = drive.Root };
                item.SubItems.Add("Drive");
                item.SubItems.Add(drive.Ready ? AppText.FormatBytes(drive.Free) : "");
                item.SubItems.Add("");
                _files.Items.Add(item);
            }
            _status.Text = $"{drives.Count:N0} drives";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    void AddEntry(FileSystemInfo entry, string kind)
    {
        var item = new ListViewItem(entry.Name) { Tag = entry.FullName };
        item.SubItems.Add(kind);
        var size = "";
        var modified = "";
        try
        {
            modified = entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            if (kind == "File")
                size = AppText.FormatBytes(entry is FileInfo file ? file.Length : 0);
        }
        catch (Exception)
        {
            // A link or locked item can still be added without its details.
        }
        item.SubItems.Add(size);
        item.SubItems.Add(modified);
        _files.Items.Add(item);
    }

    void OpenSelection()
    {
        if (_files.SelectedItems.Count != 1)
        {
            AddSelected();
            return;
        }
        var tag = _files.SelectedItems[0].Tag as string;
        if (tag == "..")
        {
            Up();
            return;
        }
        if (string.IsNullOrEmpty(tag))
            return;
        var kind = _files.SelectedItems[0].SubItems.Count > 1 ? _files.SelectedItems[0].SubItems[1].Text : "";
        if (kind == "Folder" && Native.IsReparse(tag))
        {
            AddPaths(new[] { tag });
            _status.Text = $"Added the link. Its target was not opened. {_chosen.Count:N0} items selected";
            return;
        }
        if (kind is "Folder" or "Drive")
            ShowDir(tag);
        else
            AddPaths(new[] { tag });
    }

    void GoAddress()
    {
        var text = _address.Text.Trim();
        if (text.Equals("this pc", StringComparison.OrdinalIgnoreCase) || text.Equals("computer", StringComparison.OrdinalIgnoreCase) || text.Length == 0)
        {
            ShowDir(null);
            return;
        }
        if (Native.IsReparse(text))
        {
            AddPaths(new[] { text });
            _status.Text = $"Added the link. Its target was not opened. {_chosen.Count:N0} items selected";
            return;
        }
        if (Directory.Exists(text))
            ShowDir(Path.GetFullPath(text));
        else if (File.Exists(text))
        {
            AddPaths(new[] { text });
            var parent = Path.GetDirectoryName(Path.GetFullPath(text));
            if (parent != null)
                ShowDir(parent);
        }
        else
            _status.Text = "That path does not exist.";
    }

    void Up()
    {
        if (_current == null)
            return;
        if (PathRules.IsDriveRoot(_current))
        {
            ShowDir(null);
            return;
        }
        var parent = Path.GetDirectoryName(_current.TrimEnd('\\'));
        ShowDir(string.IsNullOrEmpty(parent) ? null : parent);
    }

    void AddSelected()
    {
        var paths = new List<string>();
        foreach (ListViewItem item in _files.SelectedItems)
        {
            if (item.Tag is string path && path != "..")
                paths.Add(path);
        }
        AddPaths(paths);
    }

    void AddCurrentFolder()
    {
        if (_current != null)
            AddPaths(new[] { _current });
    }

    void AddPaths(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(_chosen.Select(path => path.ToUpperInvariant()));
        foreach (var path in paths)
        {
            string absolute;
            try { absolute = Path.GetFullPath(path); }
            catch (Exception) { continue; }
            if (!existing.Add(absolute.ToUpperInvariant()))
                continue;
            try
            {
                _ = File.GetAttributes(absolute);
            }
            catch (Exception)
            {
                existing.Remove(absolute.ToUpperInvariant());
                continue;
            }
            _chosen.Add(absolute);
            _cart.Items.Add(absolute);
        }
        RefreshSelect();
        _status.Text = $"{_chosen.Count:N0} items selected";
    }

    void RemoveSelected()
    {
        var indexes = _cart.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
        foreach (var index in indexes)
        {
            _chosen.RemoveAt(index);
            _cart.Items.RemoveAt(index);
        }
        RefreshSelect();
        _status.Text = $"{_chosen.Count:N0} items selected";
    }

    void RefreshSelect()
    {
        var ready = _chosen.Count > 0;
        _select.Enabled = ready;
        _select.BackColor = ready ? Theme.Pine : Theme.Disabled;
        _select.ForeColor = ready ? Theme.OnAccent : Theme.DisabledText;
    }

    static Button SmallButton(string text, Action click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.White,
            ForeColor = Theme.Ink,
            Margin = new Padding(0, 0, 8, 0),
        };
        button.FlatAppearance.BorderColor = Theme.Line;
        button.MouseEnter += (_, _) => button.FlatAppearance.BorderColor = Theme.Pine;
        button.MouseLeave += (_, _) => button.FlatAppearance.BorderColor = Theme.Line;
        button.Click += (_, _) => click();
        return button;
    }
}
