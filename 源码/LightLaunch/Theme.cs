using System.Drawing.Drawing2D;

namespace LightLaunch;

// 精简软件配色 v1：保持与工作区配色规范相同的语义及色值。
internal static class Palette
{
    public static readonly Color Paper = ColorTranslator.FromHtml("#F4F0E6");
    public static readonly Color Shell = ColorTranslator.FromHtml("#EDE7DC");
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceAlt = ColorTranslator.FromHtml("#F4F0E7");
    public static readonly Color Hover = ColorTranslator.FromHtml("#EAE4D9");
    public static readonly Color Border = ColorTranslator.FromHtml("#E3DDD2");
    public static readonly Color BorderStrong = ColorTranslator.FromHtml("#D2CABA");
    public static readonly Color Ink = ColorTranslator.FromHtml("#1F2621");
    public static readonly Color Muted = ColorTranslator.FromHtml("#57605B");
    public static readonly Color Faint = ColorTranslator.FromHtml("#8A938D");
    public static readonly Color Accent = ColorTranslator.FromHtml("#0F766E");
    public static readonly Color AccentHover = ColorTranslator.FromHtml("#0C625C");
    public static readonly Color AccentSoft = ColorTranslator.FromHtml("#E4F0ED");
    public static readonly Color AccentLine = ColorTranslator.FromHtml("#B7D6D0");
    public static readonly Color Warn = ColorTranslator.FromHtml("#A8620C");
    public static readonly Color WarnSoft = ColorTranslator.FromHtml("#FBF1E1");
    public static readonly Color Danger = ColorTranslator.FromHtml("#B3261E");

    public static Button Button(string text, bool primary = false, int width = 104) => new ThemeButton(primary) {
        Text = text, Width = width, Height = 38, Cursor = Cursors.Hand,
        Margin = new Padding(0, 0, 10, 0), UseVisualStyleBackColor = false,
        BackColor = primary ? Accent : Surface, ForeColor = primary ? Surface : Ink,
        FlatStyle = FlatStyle.Flat, AccessibleName = text
    };

    public static void Style(Form f, string title, Size size)
    {
        f.SuspendLayout();
        f.Text = title; f.Font = new Font("Microsoft YaHei UI", 10); f.ForeColor = Ink;
        f.BackColor = Paper; f.ClientSize = size; f.StartPosition = FormStartPosition.CenterParent;
        f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
        f.AutoScaleMode = AutoScaleMode.Dpi; f.AutoScaleDimensions = new SizeF(96, 96);
        f.Load += (_, _) => FitWindow(f);
    }

    public static void FitWindow(Form form)
    {
        if (!form.TopLevel) return;
        var area = Screen.FromControl(form).WorkingArea;
        int margin = (int)Math.Round(12 * form.DeviceDpi / 96f);
        int width = Math.Min(form.Width, area.Width - margin * 2), height = Math.Min(form.Height, area.Height - margin * 2);
        form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, width), Math.Min(form.MinimumSize.Height, height));
        form.Size = new Size(width, height);
        form.Location = new Point(Math.Clamp(form.Left, area.Left + margin, Math.Max(area.Left + margin, area.Right - width - margin)),
            Math.Clamp(form.Top, area.Top + margin, Math.Max(area.Top + margin, area.Bottom - height - margin)));
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.EnableHeadersVisualStyles = false; grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None; grid.GridColor = Border;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = 40; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle {
            BackColor = SurfaceAlt, ForeColor = Muted, SelectionBackColor = SurfaceAlt,
            SelectionForeColor = Muted, Padding = new Padding(8, 0, 8, 0), Font = new Font("Microsoft YaHei UI", 9)
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle {
            BackColor = Surface, ForeColor = Ink, SelectionBackColor = AccentSoft,
            SelectionForeColor = Ink, Padding = new Padding(8), Font = new Font("Microsoft YaHei UI", 10)
        };
        grid.RowTemplate.Height = 54;
    }

    public static void FinishDialog(Form form)
    {
        void StyleChildren(Control parent)
        {
            // Pin automatic rows before wrapping or hiding fields. Otherwise WinForms
            // reflows later controls into the hidden record rows, including the buttons.
            if (parent is TableLayoutPanel table && table.ColumnCount == 1) {
                int row = 0;
                foreach (Control child in table.Controls) {
                    var cell = table.GetCellPosition(child);
                    if (cell.Row < 0) table.SetCellPosition(child, new TableLayoutPanelCellPosition(0, row));
                    row = Math.Max(row + 1, cell.Row + 1);
                }
            }
            foreach (Control c in parent.Controls.Cast<Control>().ToArray()) {
                if (c is TextBox text) {
                    text.ForeColor = Ink; text.BackColor = text.ReadOnly ? SurfaceAlt : Surface;
                    text.BorderStyle = BorderStyle.None;
                    WrapField(text, text.Multiline ? new Padding(12, 10, 12, 8) : new Padding(10, 5, 10, 3));
                } else if (c is ListBox list) {
                    if(list.DrawMode!=DrawMode.Normal)continue;
                    list.BackColor = Surface; list.ForeColor = Ink; list.BorderStyle = BorderStyle.None;
                    list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 32;
                    list.DrawItem += (_, e) => {
                        if (e.Index < 0) return;
                        bool selected = (e.State & DrawItemState.Selected) != 0;
                        using var fill = new SolidBrush(selected ? AccentSoft : Surface); e.Graphics.FillRectangle(fill, e.Bounds);
                        TextRenderer.DrawText(e.Graphics, list.Items[e.Index]?.ToString(), list.Font,
                            new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 18, e.Bounds.Height),
                            selected ? Accent : Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
                    };
                    WrapField(list, new Padding(1));
                } else if (c is ComboBox combo) {
                    combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Surface; combo.ForeColor = Ink;
                } else if (c is DataGridView grid) StyleGrid(grid);
                else if (c is not System.Windows.Forms.Button) StyleChildren(c);
            }
        }
        StyleChildren(form);
        form.ResumeLayout(true);
        void ScaleGrids(Control parent)
        {
            foreach (Control c in parent.Controls) {
                if (c is DataGridView grid) {
                    grid.ColumnHeadersHeight = (int)Math.Round(40 * form.DeviceDpi / 96f);
                    foreach (DataGridViewColumn column in grid.Columns)
                        if (column.AutoSizeMode == DataGridViewAutoSizeColumnMode.None) column.Width = (int)Math.Round(column.Width * form.DeviceDpi / 96f);
                } else ScaleGrids(c);
            }
        }
        ScaleGrids(form);
    }

    private static void WrapField(Control field, Padding padding)
    {
        if (field.Parent is not TableLayoutPanel table) return;
        var position = table.GetPositionFromControl(field);
        int span = table.GetColumnSpan(field), rowSpan = table.GetRowSpan(field);
        var frame = new SurfacePanel(9) { Dock = field.Dock, Margin = field.Margin, Padding = padding, BackColor = field.BackColor };
        table.Controls.Remove(field); field.Dock = DockStyle.Fill; field.Margin = Padding.Empty;
        frame.Controls.Add(field); table.Controls.Add(frame, position.Column, position.Row);
        table.SetColumnSpan(frame, span); table.SetRowSpan(frame, rowSpan);
        field.Enter += (_, _) => { frame.BorderColor = Accent; frame.Invalidate(); };
        field.Leave += (_, _) => { frame.BorderColor = BorderStrong; frame.Invalidate(); };
    }

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90); path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}

internal sealed class SurfacePanel : Panel
{
    public Color BorderColor { get; set; } = Palette.Border;
    private readonly int radius;
    public SurfacePanel(int radius = 13)
    {
        this.radius = radius; BackColor = Palette.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Palette.Paper);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Palette.Rounded(new RectangleF(.5f, .5f, Math.Max(0, Width - 1), Math.Max(0, Height - 1)), radius * DeviceDpi / 96f);
        using var brush = new SolidBrush(BackColor); e.Graphics.FillPath(brush, path);
        using var pen = new Pen(BorderColor); e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class ThemeButton : Button
{
    private readonly bool primary;
    private bool hover, pressed;
    public ThemeButton(bool primary)
    {
        this.primary = primary;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatAppearance.BorderSize = 0;
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Palette.Paper); g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = !Enabled ? Palette.SurfaceAlt : primary ? hover || pressed ? Palette.AccentHover : Palette.Accent : pressed ? Palette.Hover : hover ? Palette.SurfaceAlt : Palette.Surface;
        Color line = Enabled && primary ? fill : hover && Enabled ? Palette.BorderStrong : Palette.Border;
        using var path = Palette.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 9 * DeviceDpi / 96f);
        using var brush = new SolidBrush(fill); g.FillPath(brush, path);
        using var pen = new Pen(line); g.DrawPath(pen, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, !Enabled ? Palette.Faint : primary ? Palette.Surface : ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) {
            var focus = ClientRectangle; focus.Inflate(-5, -5);
            ControlPaint.DrawFocusRectangle(g, focus, primary ? Palette.Surface : Palette.Accent, fill);
        }
    }
}

internal sealed class BrandMark : Control
{
    public BrandMark() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Palette.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 10 * DeviceDpi / 96f);
        using var fill = new SolidBrush(Palette.Accent); e.Graphics.FillPath(fill, path);
        using var pen = new Pen(Palette.Surface, Math.Max(2, Width / 17f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, new[] { new PointF(Width * .39f, Height * .28f), new PointF(Width * .65f, Height * .5f), new PointF(Width * .39f, Height * .72f) });
    }
}
