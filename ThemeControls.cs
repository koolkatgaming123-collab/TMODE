using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Windows.Forms;

namespace TourneyMode;

internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(10, 15, 24);
    public static readonly Color Sidebar = Color.FromArgb(13, 20, 32);
    public static readonly Color Surface = Color.FromArgb(18, 28, 43);
    public static readonly Color Raised = Color.FromArgb(24, 37, 55);
    public static readonly Color Border = Color.FromArgb(39, 57, 79);
    public static readonly Color Text = Color.FromArgb(232, 241, 252);
    public static readonly Color Muted = Color.FromArgb(143, 163, 187);
    public static readonly Color Cyan = Color.FromArgb(53, 211, 239);
    public static readonly Color Blue = Color.FromArgb(55, 128, 246);
    public static readonly Color Amber = Color.FromArgb(245, 190, 100);
    public static readonly Color Green = Color.FromArgb(96, 218, 169);
    public static readonly Color Red = Color.FromArgb(245, 132, 136);
    public static Color Backdrop(Control? parent)
    {
        while (parent != null) { if (parent.BackColor.A == 255) return parent.BackColor; parent = parent.Parent; }
        return Background;
    }
    public static GraphicsPath Round(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
    public static Label Label(string text, float size = 10, bool bold = false, Color? color = null) => new()
    {
        Text = text, AutoSize = true, ForeColor = color ?? Text, BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 0, 0, 8)
    };
    public static TableLayoutPanel Stack(int padding = 0)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 0, Padding = new Padding(padding), Margin = Padding.Empty, BackColor = Color.Transparent };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }
    public static void Add(TableLayoutPanel stack, Control control)
    {
        int row = stack.RowCount++; stack.RowStyles.Add(new RowStyle(SizeType.AutoSize)); stack.Controls.Add(control, 0, row);
    }
}

internal sealed class ThemeCard : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 14;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = UiTheme.Surface;
    public ThemeCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Surface; Padding = new Padding(22); Margin = new Padding(0, 0, 0, 16);
    }
    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(UiTheme.Backdrop(Parent));
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = UiTheme.Round(bounds, Radius); using var brush = new SolidBrush(Fill); using var pen = new Pen(UiTheme.Border);
        e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); base.OnPaint(e);
    }
}

internal sealed class ThemeButton : Button
{
    private bool _hover;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Navigation { get; set; }
    public ThemeButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
        Font = new Font("Segoe UI", 10, FontStyle.Bold); ForeColor = UiTheme.Text; BackColor = UiTheme.Raised;
        Height = 42; Width = 150; Cursor = Cursors.Hand; Margin = new Padding(0, 0, 10, 0);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(UiTheme.Backdrop(Parent));
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = !Enabled ? UiTheme.Surface : Primary ? (_hover ? Color.FromArgb(75, 148, 255) : UiTheme.Blue)
            : Selected ? Color.FromArgb(27, 53, 78) : _hover ? UiTheme.Raised : Navigation ? UiTheme.Sidebar : UiTheme.Raised;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = UiTheme.Round(bounds, Navigation ? 9 : 8); using var brush = new SolidBrush(fill);
        e.Graphics.FillPath(brush, path);
        if (!Navigation && !Primary) { using var pen = new Pen(UiTheme.Border); e.Graphics.DrawPath(pen, path); }
        if (Selected) { using var bar = new SolidBrush(UiTheme.Cyan); e.Graphics.FillRectangle(bar, 0, 11, 3, Math.Max(1, Height - 22)); }
        var textBounds = Navigation ? new Rectangle(19, 0, Width - 28, Height) : ClientRectangle;
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, !Enabled ? UiTheme.Muted : Selected ? UiTheme.Cyan : UiTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | (Navigation ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
    }
}

internal interface IThemeChoice { string DisplayValue { get; } }

internal sealed class ThemeSwitch : CheckBox
{
    public ThemeSwitch()
    {
        AutoSize = false; Size = new Size(78, 34); Margin = new Padding(0, 0, 14, 0); Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.CheckButton; Font = new Font("Segoe UI", 8, FontStyle.Bold);
    }
    public override Size GetPreferredSize(Size proposedSize) => new(78, 34);
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(UiTheme.Backdrop(Parent));
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(2, 3, Width - 5, Height - 7);
        using var path = UiTheme.Round(bounds, 14); using var fill = new SolidBrush(Enabled && Checked ? UiTheme.Blue : UiTheme.Raised); using var border = new Pen(Focused ? UiTheme.Cyan : UiTheme.Border);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
        int diameter = bounds.Height - 8; int x = Checked ? bounds.Right - diameter - 4 : bounds.Left + 4;
        using var thumb = new SolidBrush(Enabled ? UiTheme.Text : UiTheme.Muted); e.Graphics.FillEllipse(thumb, x, bounds.Top + 4, diameter, diameter);
        var text = Checked ? new Rectangle(bounds.Left + 7, bounds.Top, 31, bounds.Height) : new Rectangle(bounds.Left + 29, bounds.Top, 36, bounds.Height);
        TextRenderer.DrawText(e.Graphics, Checked ? "ON" : "OFF", Font, text, Enabled ? UiTheme.Text : UiTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
    }
}

internal sealed class ThemeChoice<T> : Control, IThemeChoice where T : struct, Enum
{
    private T _value;
    private ContextMenuStrip? _menu;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<T, string> Display { get; set; } = value => value.ToString();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public T Value { get => _value; set { if (EqualityComparer<T>.Default.Equals(_value, value)) return; _value = value; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
    public event EventHandler? ValueChanged;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string DisplayValue => Display(Value);
    public ThemeChoice()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Height = 40; Width = 210; TabStop = true; Cursor = Cursors.Hand; Font = new Font("Segoe UI", 9.5f);
        BackColor = UiTheme.Raised; ForeColor = UiTheme.Text; AccessibleRole = AccessibleRole.ComboBox;
    }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Enter or Keys.Space or Keys.Down or Keys.Up or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override AccessibleObject CreateAccessibilityInstance() => new ChoiceAccessibility(this);
    private sealed class ChoiceAccessibility(ThemeChoice<T> owner) : ControlAccessibleObject(owner)
    {
        public override string? Value { get => owner.DisplayValue; set { } }
        public override string? DefaultAction => "Open choices";
        public override void DoDefaultAction() { if (owner.Enabled) owner.OpenChoices(); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiTheme.Backdrop(Parent));
        using var path = UiTheme.Round(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 7);
        using var fill = new SolidBrush(UiTheme.Raised); using var border = new Pen(Focused ? UiTheme.Cyan : UiTheme.Border);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(e.Graphics, DisplayValue, Font, new Rectangle(12, 0, Math.Max(0, Width - 42), Height),
            Enabled ? UiTheme.Text : UiTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var arrow = new Pen(UiTheme.Muted, 1.5f);
        e.Graphics.DrawLines(arrow, [new Point(Width - 24, Height / 2 - 2), new Point(Width - 19, Height / 2 + 3), new Point(Width - 14, Height / 2 - 2)]);
    }
    private void OpenChoices()
    {
        if (!Enabled) return;
        _menu?.Dispose(); _menu = new ContextMenuStrip { BackColor = UiTheme.Raised, ForeColor = UiTheme.Text, ShowImageMargin = false, Font = Font };
        foreach (T value in Enum.GetValues<T>())
        {
            var item = new ToolStripMenuItem(Display(value)) { Checked = EqualityComparer<T>.Default.Equals(Value, value), ForeColor = UiTheme.Text };
            item.Click += (_, _) => Value = value; _menu.Items.Add(item);
        }
        _menu.Show(this, new Point(0, Height));
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); OpenChoices(); } }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down) { e.Handled = true; e.SuppressKeyPress = true; OpenChoices(); }
        else if (e.KeyCode is Keys.Up or Keys.Home or Keys.End)
        {
            var values = Enum.GetValues<T>(); int index = Array.IndexOf(values, Value);
            Value = e.KeyCode == Keys.Home ? values[0] : e.KeyCode == Keys.End ? values[^1] : values[Math.Max(0, index - 1)];
            e.Handled = true; e.SuppressKeyPress = true;
        }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) _menu?.Dispose(); base.Dispose(disposing); }
}
