namespace Hygieia;

sealed class ConfirmForm : Form
{
    readonly TextBox _word = new();
    readonly Button _confirm = new();

    ConfirmForm(string title, IReadOnlyList<string> paragraphs, IReadOnlyList<string> paths)
    {
        Text = title;
        BackColor = Theme.Paper;
        Font = Theme.Ui(10);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        HandleCreated += (_, _) => Theme.DarkTitle(this);
        ClientSize = new Size(560, 460);
        MinimumSize = new Size(520, 380);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 18), BackColor = Theme.Paper };
        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = Theme.Ui(16, FontStyle.Bold),
            ForeColor = Theme.Ink,
            BackColor = Theme.Paper,
            Padding = new Padding(0, 0, 0, 8),
        };
        var text = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            ForeColor = Theme.Ink,
            BackColor = Theme.Paper,
            Text = string.Join(Environment.NewLine + Environment.NewLine, paragraphs),
            Padding = new Padding(0, 0, 0, 8),
        };
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
        };
        Theme.StyleListBox(list);
        var shown = paths.Take(12).ToList();
        list.Items.AddRange(shown.Cast<object>().ToArray());
        if (paths.Count > shown.Count)
            list.Items.Add($"and {paths.Count - shown.Count} more");

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 92, BackColor = Theme.Paper };
        var prompt = new Label
        {
            Text = "Type SCRUB to confirm. This cannot be undone.",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Theme.Muted,
            BackColor = Theme.Paper,
        };
        _word.Dock = DockStyle.Top;
        _word.Font = Theme.Ui(11);
        Theme.StyleField(_word);
        _word.TextChanged += (_, _) => UpdateConfirm();
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Paper,
            Padding = new Padding(0, 8, 0, 0),
        };
        _confirm.Text = "Scrub";
        _confirm.Enabled = false;
        _confirm.AutoSize = true;
        _confirm.Padding = new Padding(12, 6, 12, 6);
        _confirm.FlatStyle = FlatStyle.Flat;
        _confirm.FlatAppearance.BorderSize = 0;
        _confirm.BackColor = Theme.Disabled;
        _confirm.ForeColor = Theme.DisabledText;
        _confirm.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            Padding = new Padding(12, 6, 12, 6),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.White,
            ForeColor = Theme.Ink,
            DialogResult = DialogResult.Cancel,
        };
        cancel.FlatAppearance.BorderColor = Theme.Line;
        buttons.Controls.Add(_confirm);
        buttons.Controls.Add(cancel);
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(_word);
        bottom.Controls.Add(prompt);

        body.Controls.Add(list);
        body.Controls.Add(text);
        body.Controls.Add(titleLabel);
        Controls.Add(body);
        Controls.Add(bottom);
        CancelButton = cancel;
        AcceptButton = _confirm;
    }

    void UpdateConfirm()
    {
        var ready = _word.Text == "SCRUB";
        _confirm.Enabled = ready;
        _confirm.BackColor = ready ? Theme.Danger : Theme.Disabled;
        _confirm.ForeColor = ready ? Color.White : Theme.DisabledText;
    }

    public static bool Ask(IWin32Window owner, string title, IReadOnlyList<string> paragraphs, IReadOnlyList<string> paths)
    {
        using var dialog = new ConfirmForm(title, paragraphs, paths);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}
