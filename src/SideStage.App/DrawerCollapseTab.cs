/*
 * 文件名称：DrawerCollapseTab.cs
 * 文件职责：在右侧设置栏的左缘提供固定收起入口，鼠标、键盘及辅助技术共用标准按钮行为。
 * 布局约定：圆钮跨坐在侧栏分隔线上，圆心对齐侧栏中点，圆外的输入交给下方控件。
 */
using System.Drawing.Drawing2D;

namespace SideStage.App;

internal sealed class DrawerCollapseTab : Button
{
    private const int Diameter = 28;
    private static readonly Color IdleFace = Color.FromArgb(250, 246, 232);
    private static readonly Color Rim = Color.FromArgb(201, 181, 149);
    private readonly PaperHintBubble _hint;
    private bool _hovered, _spaceDown;

    /// <summary>建立侧栏把手并复用日志气泡的样式。提示只供阅读，鼠标与键盘继续操作原按钮。</summary>
    internal DrawerCollapseTab(Color paper, Color ink, Font hintFont)
    {
        AccessibleName = "收起侧栏";
        ForeColor = ink;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 219, 187);
        FlatAppearance.MouseDownBackColor = Color.FromArgb(222, 197, 157);
        BackColor = paper;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Margin = Padding.Empty;
        DoubleBuffered = true;
        _hint = new PaperHintBubble(this) { AutoClose = false };
        var caption = new Label
        {
            Text = AccessibleName, Font = hintFont, AutoSize = true, Margin = Padding.Empty,
            BackColor = PaperHintBubble.Surface, ForeColor = Color.FromArgb(249, 243, 229),
        };
        _hint.Items.Add(new ToolStripControlHost(caption) { Margin = Padding.Empty, Padding = Padding.Empty });
        MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _hovered = false; _hint.Close(); Invalidate(); };
        MouseHover += (_, _) =>
        {
            if (!_hovered || !Visible || Capture || _hint.Visible) return;
            caption.Padding = DrawerMetrics.Space(this, 9, 5, 9, 6);
            // 提示出现在把手左下方，右侧设置项仍可直接阅读和操作。
            _hint.Show(this, new Point(Width, Height), ToolStripDropDownDirection.BelowLeft);
            _hint.UpdateEdge();
        };
        MouseDown += (_, _) => _hint.Close();
        MouseCaptureChanged += (_, _) => Invalidate();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Space) { _spaceDown = true; Invalidate(); } };
        KeyUp += (_, e) => { if (e.KeyCode == Keys.Space) { _spaceDown = false; Invalidate(); } };
        LostFocus += (_, _) => { _spaceDown = false; Invalidate(); };
        LocationChanged += (_, _) => _hint.Close();
        VisibleChanged += (_, _) =>
        {
            if (Visible) return;
            _hovered = _spaceDown = false;
            _hint.Close();
        };
    }

    /// <summary>按当前显示比例紧贴侧栏中点。极窄窗口会把露出的部分移回工作区，仍可收起。</summary>
    internal void PlaceAt(Control drawer)
    {
        int diameter = drawer.LogicalToDeviceUnits(Diameter);
        SetBounds(Math.Max(0, drawer.Left - diameter / 2),
            drawer.Top + (drawer.Height - diameter) / 2, diameter, diameter);
    }

    /// <summary>用浅纸色圆面和细铜边标示入口，悬停和按下时整枚加深，键盘焦点沿圆周显示。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        bool keyboardFocus = Focused && ShowFocusCues;
        bool pressed = _spaceDown || (_hovered && Capture && MouseButtons.HasFlag(MouseButtons.Left));
        Color faceColor = IdleFace;
        if (pressed) faceColor = FlatAppearance.MouseDownBackColor;
        else if (_hovered || keyboardFocus) faceColor = FlatAppearance.MouseOverBackColor;
        float scale = DeviceDpi / 96f;
        float stroke = (keyboardFocus ? 2 : 1) * scale;
        RectangleF circle = new(stroke / 2, stroke / 2, Width - stroke, Height - stroke);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.Clear(BackColor);
        using SolidBrush face = new(faceColor);
        using Pen rim = new(keyboardFocus ? ForeColor : Rim, stroke);
        e.Graphics.FillEllipse(face, circle);
        e.Graphics.DrawEllipse(rim, circle);
        // 两枚箭头以圆心为共同基准，跨屏时同时缩放间距、线宽和端点。
        float cx = Width / 2f, cy = Height / 2f;
        using Pen pen = new(ForeColor, 1.6f * scale)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
        };
        for (int i = 0; i < 2; i++)
        {
            float x = cx + (5 * i - 4.25f) * scale;
            e.Graphics.DrawLines(pen, new PointF[] {
                new(x, cy - 3.5f * scale), new(x + 3.5f * scale, cy), new(x, cy + 3.5f * scale),
            });
        }
    }

    /// <summary>让按钮的原生命中范围和拖动截图都使用圆形，四角继续显示纸面或远程桌面。</summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using GraphicsPath path = new();
        path.AddEllipse(ClientRectangle);
        Region = new Region(path);
    }

    /// <summary>随把手一起释放独立提示窗口及其圆角装饰层。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _hint.Dispose();
        base.Dispose(disposing);
    }
}
