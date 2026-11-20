using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Hygieia;

static class Theme
{
    public static readonly Color Paper = Color.FromArgb(0x07, 0x0A, 0x10);
    public static readonly Color White = Color.FromArgb(0x12, 0x18, 0x26);
    public static readonly Color Field = Color.FromArgb(0x0C, 0x12, 0x1C);
    public static readonly Color Ink = Color.FromArgb(0xE7, 0xF0, 0xFF);
    public static readonly Color Muted = Color.FromArgb(0x93, 0xA0, 0xB8);
    public static readonly Color Label = Color.FromArgb(0x67, 0xE8, 0xF9);
    public static readonly Color Line = Color.FromArgb(0x24, 0x33, 0x48);
    public static readonly Color Danger = Color.FromArgb(0xE1, 0x1D, 0x48);
    public static readonly Color DangerDark = Color.FromArgb(0xBE, 0x12, 0x3C);
    public static readonly Color Pine = Color.FromArgb(0x22, 0xD3, 0xEE);
    public static readonly Color PineDark = Color.FromArgb(0x06, 0xB6, 0xD4);
    public static readonly Color OnAccent = Color.FromArgb(0x04, 0x14, 0x1C);
    public static readonly Color Select = Color.FromArgb(0x12, 0x3A, 0x48);
    public static readonly Color Disabled = Color.FromArgb(0x1E, 0x27, 0x36);
    public static readonly Color DisabledText = Color.FromArgb(0x66, 0x73, 0x8A);

    static readonly MemoryStream IconBytes = new();
    static readonly Icon AppIcon = LoadAppIcon();
    static AllowDarkModeForWindow? _allowDark;

    public static void ApplyIcon(Form form) => form.Icon = AppIcon;

    static Icon LoadAppIcon()
    {
        using var packed = typeof(Theme).Assembly.GetManifestResourceStream("Hygieia.App.ico");
        if (packed == null)
            return (Icon)SystemIcons.Application.Clone();
        packed.CopyTo(IconBytes);
        IconBytes.Position = 0;
        return new Icon(IconBytes, 32, 32);
    }

    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style, GraphicsUnit.Point);
    public static Font Display(float size) => new("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Point);

    public static void PreferDark()
    {
        var theme = LoadLibrary("uxtheme.dll");
        if (theme == IntPtr.Zero)
            return;
        var preferred = GetProcAddress(theme, (IntPtr)135);
        if (preferred != IntPtr.Zero)
            Marshal.GetDelegateForFunctionPointer<SetPreferredAppMode>(preferred)(2);
        var allow = GetProcAddress(theme, (IntPtr)133);
        if (allow != IntPtr.Zero)
            _allowDark = Marshal.GetDelegateForFunctionPointer<AllowDarkModeForWindow>(allow);
        var flush = GetProcAddress(theme, (IntPtr)136);
        if (flush != IntPtr.Zero)
            Marshal.GetDelegateForFunctionPointer<FlushMenuThemes>(flush)();
    }

    public static void DarkTitle(Form form)
    {
        if (!form.IsHandleCreated)
            return;
        var use = 1;
        DwmSetWindowAttribute(form.Handle, 20, ref use, sizeof(int));
        AllowDark(form.Handle);
    }

    public static void StyleCombo(ComboBox combo)
    {
        combo.FlatStyle = FlatStyle.Flat;
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.BackColor = Field;
        combo.ForeColor = Ink;
        combo.ItemHeight = Math.Max(22, TextRenderer.MeasureText("Ag", combo.Font).Height + 6);
        combo.DrawItem += (_, e) => PaintComboItem(combo, e);
        combo.DropDown += (_, _) => ThemeDropList(combo);
        combo.GotFocus += (_, _) => combo.Invalidate();
        combo.LostFocus += (_, _) => combo.Invalidate();
        var chrome = new ComboChrome();
        void Hook() => chrome.AssignHandle(combo.Handle);
        if (combo.IsHandleCreated)
            Hook();
        else
            combo.HandleCreated += (_, _) => Hook();
        combo.Disposed += (_, _) => chrome.ReleaseHandle();
    }

    public static void StyleField(TextBoxBase box)
    {
        box.BackColor = Field;
        box.ForeColor = Ink;
        box.BorderStyle = BorderStyle.FixedSingle;
        DarkExplorer(box);
    }

    public static void StyleListBox(ListBox box)
    {
        box.BackColor = Field;
        box.ForeColor = Ink;
        box.BorderStyle = BorderStyle.FixedSingle;
        DarkExplorer(box);
    }

    public static void StyleCheck(CheckBox box)
    {
        box.UseVisualStyleBackColor = false;
        box.FlatStyle = FlatStyle.Flat;
        box.ForeColor = Ink;
        box.FlatAppearance.BorderColor = Label;
        box.FlatAppearance.BorderSize = 1;
        box.FlatAppearance.CheckedBackColor = Pine;
        box.FlatAppearance.MouseOverBackColor = Select;
        box.FlatAppearance.MouseDownBackColor = Select;
    }

    public static void StyleList(ListView list)
    {
        list.BackColor = Field;
        list.ForeColor = Ink;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.OwnerDraw = true;
        list.DrawColumnHeader += (_, e) =>
        {
            using var back = new SolidBrush(White);
            e.Graphics.FillRectangle(back, e.Bounds);
            var header = e.Header;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            flags |= header == null ? TextFormatFlags.Left : header.TextAlign switch
            {
                HorizontalAlignment.Right => TextFormatFlags.Right,
                HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
                _ => TextFormatFlags.Left,
            };
            var bounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, header?.Text ?? "", list.Font, bounds, Label, flags);
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        };
        list.DrawItem += (_, e) => e.DrawDefault = false;
        list.DrawSubItem += (_, e) =>
        {
            var item = e.Item;
            if (item == null)
                return;
            var selected = item.Selected;
            using var back = new SolidBrush(selected ? Select : Field);
            e.Graphics.FillRectangle(back, e.Bounds);
            var text = e.SubItem?.Text ?? "";
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            flags |= e.Header?.TextAlign switch
            {
                HorizontalAlignment.Right => TextFormatFlags.Right,
                HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
                _ => TextFormatFlags.Left,
            };
            var bounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, list.Font, bounds, selected ? Pine : Ink, flags);
        };
        var headerPainter = new HeaderPainter(list);
        void Hook()
        {
            DarkExplorer(list);
            var header = SendMessage(list.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);
            if (header == IntPtr.Zero)
                return;
            SetWindowTheme(header, "", "");
            headerPainter.AssignHandle(header);
        }
        if (list.IsHandleCreated)
            Hook();
        else
            list.HandleCreated += (_, _) => Hook();
        list.Disposed += (_, _) => headerPainter.ReleaseHandle();
    }

    public static void StyleProgress(ProgressBar bar)
    {
        void Apply()
        {
            SetWindowTheme(bar.Handle, "", "");
            bar.BackColor = Field;
            bar.ForeColor = Pine;
        }
        if (bar.IsHandleCreated)
            Apply();
        else
            bar.HandleCreated += (_, _) => Apply();
    }

    static void PaintComboItem(ComboBox combo, DrawItemEventArgs e)
    {
        var edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        var hot = !edit && (e.State & DrawItemState.Selected) != 0;
        var back = combo.Enabled ? (hot ? Select : Field) : Disabled;
        var fore = combo.Enabled ? (hot ? Pine : Ink) : DisabledText;
        using var brush = new SolidBrush(back);
        e.Graphics.FillRectangle(brush, e.Bounds);
        if (e.Index < 0)
            return;
        var text = combo.GetItemText(combo.Items[e.Index]);
        var bounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            text,
            e.Font ?? combo.Font,
            bounds,
            fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    static void ThemeDropList(ComboBox combo)
    {
        var info = new ComboBoxInfo { Size = Marshal.SizeOf<ComboBoxInfo>() };
        if (!GetComboBoxInfo(combo.Handle, ref info) || info.List == IntPtr.Zero)
            return;
        AllowDark(info.List);
        SetWindowTheme(info.List, "DarkMode_Explorer", null);
    }

    static void DarkExplorer(Control control)
    {
        void Apply()
        {
            AllowDark(control.Handle);
            SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        if (control.IsHandleCreated)
            Apply();
        else
            control.HandleCreated += (_, _) => Apply();
    }

    static void AllowDark(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
            _allowDark?.Invoke(hwnd, true);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int SetPreferredAppMode(int mode);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void FlushMenuThemes();

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibrary(string name);

    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string subApp, string? subIdList);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool GetComboBoxInfo(IntPtr hwnd, ref ComboBoxInfo info);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    static extern int GetScrollPos(IntPtr hwnd, int bar);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ComboBoxInfo
    {
        public int Size;
        public Rect Item;
        public Rect Button;
        public int ButtonState;
        public IntPtr Combo;
        public IntPtr Edit;
        public IntPtr List;
    }

    sealed class ComboChrome : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F)
                Paint();
        }

        void Paint()
        {
            var combo = Control.FromHandle(Handle) as ComboBox;
            if (combo == null || !combo.IsHandleCreated)
                return;
            var info = new ComboBoxInfo { Size = Marshal.SizeOf<ComboBoxInfo>() };
            Rectangle button;
            if (GetComboBoxInfo(Handle, ref info))
                button = Rectangle.FromLTRB(info.Button.Left, info.Button.Top, info.Button.Right, info.Button.Bottom);
            else
            {
                var width = SystemInformation.VerticalScrollBarWidth;
                button = new Rectangle(Math.Max(0, combo.ClientSize.Width - width), 0, width, combo.ClientSize.Height);
            }
            using var g = Graphics.FromHwnd(Handle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = button;
            fill.Inflate(1, 0);
            using (var back = new SolidBrush(combo.Enabled ? Field : Disabled))
                g.FillRectangle(back, fill);
            var midX = button.Left + (button.Width / 2);
            var midY = button.Top + (button.Height / 2);
            using (var pen = new Pen(combo.Enabled ? Label : DisabledText, 1.7f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new[]
                {
                    new Point(midX - 4, midY - 2),
                    new Point(midX, midY + 3),
                    new Point(midX + 4, midY - 2),
                });
            }
            using var border = new Pen(combo.Focused && combo.Enabled ? Pine : Line);
            g.DrawRectangle(border, 0, 0, combo.ClientSize.Width - 1, combo.ClientSize.Height - 1);
        }
    }

    sealed class HeaderPainter : NativeWindow
    {
        readonly ListView _list;

        public HeaderPainter(ListView list) => _list = list;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0014)
            {
                Fill(m.WParam);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == 0x000F)
                Fill(IntPtr.Zero);
        }

        void Fill(IntPtr hdc)
        {
            if (!GetClientRect(Handle, out var rect))
                return;
            var used = 0;
            foreach (ColumnHeader column in _list.Columns)
                used += column.Width;
            used -= GetScrollPos(_list.Handle, 0);
            if (used >= rect.Right)
                return;
            using var g = hdc == IntPtr.Zero ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(hdc);
            using var back = new SolidBrush(White);
            g.FillRectangle(back, used, 0, rect.Right - used, rect.Bottom);
            using var pen = new Pen(Line);
            g.DrawLine(pen, used, rect.Bottom - 1, rect.Right, rect.Bottom - 1);
        }
    }
}

sealed class Card : Panel
{
    public Card()
    {
        BackColor = Theme.White;
        Padding = new Padding(12, 8, 12, 8);
        Margin = new Padding(0, 0, 0, 8);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
