/*
 * 文件名称：SettingsList.cs
 * 文件职责：将设置按功能排进同一圆角分组，提供整行操作、行内选项和二态开关。
 * 交互约定：沿用原生按钮和复选框的键盘及辅助功能语义，分隔线仅绘制在可见行之间。
 */
using System.Drawing.Drawing2D;

namespace SideStage.App;

internal sealed class SettingsList : TableLayoutPanel
{
    /// <summary>创建按内容增高的单列分组，由外层滚动区域决定行宽。</summary>
    internal SettingsList(int topGap = 2, int bottomGap = 4)
    {
        DoubleBuffered = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        DrawerMetrics.Margin(this, 0, topGap, 0, bottomGap);
        Padding = Padding.Empty;
        BackColor = PaperFieldSurface.Paper;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    /// <summary>添加独占一行的控件，隐藏行不再占用垂直空间。</summary>
    internal void AddRow(Control row)
    {
        int index = RowCount++;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.Margin = Padding.Empty;
        row.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(row, 0, index);
        WatchRowBoundary(row);
    }

    /// <summary>相邻行共享一条分隔线。指针穿过原生输入框或行边界时，让两侧都重新判断分隔线。</summary>
    private void WatchRowBoundary(Control part)
    {
        part.MouseEnter += RefreshBoundaries;
        part.MouseLeave += RefreshBoundaries;
        part.GotFocus += RefreshBoundaries;
        part.LostFocus += RefreshBoundaries;
        foreach (Control child in part.Controls) WatchRowBoundary(child);
    }

    /// <summary>刷新当前分组及其子窗口，以便离开高亮行后恢复相邻行的细线。</summary>
    private void RefreshBoundaries(object? sender, EventArgs e) => Invalidate(true);

    /// <summary>把内部分隔线画在下一行的首个像素内。两侧任一行悬停时都让出整条线。</summary>
    internal void PaintDividerAbove(Graphics graphics, Control row, Color fill)
    {
        if (row.Top <= 0 || fill != PaperFieldSurface.Paper) return;
        Point above = new(row.Left + row.Width / 2, row.Top - 1);
        Control? previous = GetChildAtPoint(above);
        if (previous == null || (previous.Enabled && previous.Bounds.Contains(PointToClient(MousePosition)))) return;
        int inset = LogicalToDeviceUnits(PaperFieldSurface.TextInset);
        using SolidBrush rule = new(Color.FromArgb(226, 216, 197));
        graphics.FillRectangle(rule, inset, 0, Math.Max(0, row.Width - 2 * inset), LogicalToDeviceUnits(1));
    }

    /// <summary>隐藏或显示条件字段后重画相邻行，使新的首尾行立即得到正确的圆角。</summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Invalidate(true);
    }

    /// <summary>在整组外沿绘出纸色圆角，内部分隔线随各行的交互状态绘制。</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        PaperFieldSurface.Paint(e.Graphics, this, BackColor);
    }
}

internal sealed class SettingsActionRow : Button
{
    private readonly bool _disclosure;

    /// <summary>建立可整行点击的操作，导航行在右边显示进入符号。</summary>
    internal SettingsActionRow(string caption, Font font, EventHandler click, bool disclosure = false)
    {
        Text = caption;
        Font = font;
        _disclosure = disclosure;
        DrawerMetrics.Bind(this, () => Height = DrawerMetrics.RowHeight(this, 36));
        BackColor = PaperFieldSurface.Paper;
        ForeColor = PaperFieldSurface.Ink;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Click += click;
    }

    /// <summary>将操作名靠左排版，为指针停留、键盘焦点与禁用状态提供反馈。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        Color fill = PaperFieldSurface.RowColor(this, Focused && ShowFocusCues, Capture && MouseButtons == MouseButtons.Left);
        PaperFieldSurface.Paint(e.Graphics, this, fill);
        Rectangle text = PaperFieldSurface.Content(this);
        TextRenderer.DrawText(e.Graphics, Text, Font, text, Enabled ? ForeColor : SystemColors.GrayText,
            PaperFieldSurface.RowText);
        if (_disclosure) TextRenderer.DrawText(e.Graphics, "›", Font, text, PaperFieldSurface.Brass,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

internal sealed class SettingsSwitch : CheckBox
{
    /// <summary>建立可整行切换的复选框，左侧为设置名，右侧为开关。</summary>
    internal SettingsSwitch(string caption, Font font)
    {
        Text = caption;
        Font = font;
        AutoSize = false;
        DrawerMetrics.Bind(this, () => Height = DrawerMetrics.RowHeight(this, 36));
        BackColor = PaperFieldSurface.Paper;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
    }

    /// <summary>用滑块位置和轨道颜色共同表示状态，键盘焦点仍然可见。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        Color fill = PaperFieldSurface.RowColor(this, Focused && ShowFocusCues, Capture && MouseButtons == MouseButtons.Left);
        PaperFieldSurface.Paint(e.Graphics, this, fill);
        Rectangle text = PaperFieldSurface.Content(this);
        int right = text.Right;
        text.Width = Math.Max(0, text.Width - LogicalToDeviceUnits(44));
        TextRenderer.DrawText(e.Graphics, Text, Font, text, Enabled ? PaperFieldSurface.Ink : SystemColors.GrayText,
            PaperFieldSurface.RowText);
        DrawTrack(e.Graphics, this, right, Checked);
    }

    /// <summary>让普通偏好与应用行共用相同的滑块尺寸、配色与对齐方式。</summary>
    internal static void DrawTrack(Graphics graphics, Control row, int right, bool active)
    {
        int w = row.LogicalToDeviceUnits(36), h = row.LogicalToDeviceUnits(20), inset = row.LogicalToDeviceUnits(2);
        Rectangle track = new(right - w, (row.Height - h) / 2, w, h);
        using GraphicsPath path = new();
        path.AddArc(track.X, track.Y, h, h, 90, 180);
        path.AddArc(track.Right - h, track.Y, h, h, 270, 180);
        path.CloseFigure();
        using SolidBrush brush = new(!row.Enabled ? Color.FromArgb(207, 202, 192) :
            active ? Color.FromArgb(71, 113, 95) : Color.FromArgb(158, 150, 134));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.FillPath(brush, path);
        graphics.FillEllipse(Brushes.White, (active ? track.Right - h : track.Left) + inset,
            track.Top + inset, h - 2 * inset, h - 2 * inset);
    }
}

/// <summary>原生编辑器与字段名共用整行底色，只有可编辑区域接受文字输入。</summary>
internal sealed class SettingsInputRow : Panel
{
    private readonly Control _input;
    private readonly Button? _trailingAction;

    /// <summary>绑定编辑器的鼠标和焦点状态。文本值、选区、密码遮罩均由原生输入控件管理。</summary>
    internal SettingsInputRow(string caption, Control input, Font font, Button? trailingAction = null)
    {
        _input = input;
        _trailingAction = trailingAction;
        Font = font;
        Text = input.AccessibleName = caption;
        DoubleBuffered = true;
        ResizeRedraw = true;
        input.BackColor = PaperFieldSurface.Paper;
        input.ForeColor = PaperFieldSurface.Ink;
        input.Font = font;
        Controls.Add(input);
        if (trailingAction != null)
        {
            Controls.Add(trailingAction);
            trailingAction.MouseEnter += RefreshField;
            trailingAction.MouseLeave += RefreshField;
            trailingAction.GotFocus += RefreshField;
            trailingAction.LostFocus += RefreshField;
        }
        DrawerMetrics.Bind(this, () => Height = Math.Max(DrawerMetrics.RowHeight(this, 39),
            input.PreferredSize.Height + LogicalToDeviceUnits(8)));
        MouseEnter += RefreshField;
        MouseLeave += RefreshField;
        input.MouseEnter += RefreshField;
        input.MouseLeave += RefreshField;
        input.GotFocus += RefreshField;
        input.LostFocus += RefreshField;
    }

    /// <summary>在同一列放置各编辑器，右边留出与开关一致的空隙，上下居中显示文字。</summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_input is null) return;
        Rectangle content = PaperFieldSurface.Content(this);
        int labelWidth = TextRenderer.MeasureText(Text, Font, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
        int left = content.Left + Math.Max(LogicalToDeviceUnits(76), labelWidth + LogicalToDeviceUnits(8));
        int right = content.Right;
        if (_trailingAction != null)
        {
            int size = Math.Min(Height - 2, LogicalToDeviceUnits(30));
            _trailingAction.SetBounds(right - size, (Height - size) / 2, size, size);
            right = _trailingAction.Left - LogicalToDeviceUnits(4);
        }
        _input.SetBounds(left, (Height - _input.Height) / 2, Math.Max(1, right - left), _input.Height);
    }

    /// <summary>在行与子窗口交界处重新计算整行命中，防止鼠标经过输入框时出现异色矩形。</summary>
    private void RefreshField(object? sender, EventArgs e)
    {
        _input.BackColor = PaperFieldSurface.RowColor(this, ContainsFocus);
        Invalidate();
    }

    /// <summary>填充同组的可见行段，并在输入框外的左栏绘制字段名。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        PaperFieldSurface.Paint(e.Graphics, this, _input.BackColor);
        TextRenderer.DrawText(e.Graphics, Text, Font, PaperFieldSurface.Content(this),
            Enabled ? PaperFieldSurface.Ink : SystemColors.GrayText, PaperFieldSurface.RowText);
    }
}
