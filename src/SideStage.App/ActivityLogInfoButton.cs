/*
 * 文件名称：ActivityLogInfoButton.cs
 * 文件职责：为活动日志与会话恢复提供带圈的信息图标、可换行的说明和可点击的链接。
 * 交互约定：悬停、点击或键盘激活都能打开气泡；指针可以从图标移到链接继续操作。
 */
using System.Drawing.Drawing2D;

namespace SideStage.App;

internal sealed class ActivityLogInfoButton : PaperInfoButton
{
    /// <summary>在日志标题旁显示本机活动记录的说明和目录链接。</summary>
    internal ActivityLogInfoButton(Font iconFont, Font bodyFont, Action openDirectory)
        : base(iconFont, bodyFont, "活动日志说明", $"{Program.DisplayName} 的活动记录", "打开完整日志文件夹", openDirectory) { }
}

/// <summary>设置和日志共用的信息入口，保留一致的边缘、键盘和悬停行为。</summary>
internal class PaperInfoButton : Button
{
    private readonly PaperHintBubble _popup;
    private readonly LinkLabel? _link;
    private readonly System.Windows.Forms.Timer _leaveTimer = new() { Interval = 300 };
    private bool _keyboardOpen;
    /// <summary>当前已展开气泡的实际屏幕范围，包含 Windows 对位置的自动调整。</summary>
    internal Rectangle BubbleBounds => _popup.Visible ? _popup.Bounds : Rectangle.Empty;

    /// <summary>按正文、原生链接、补充说明的顺序布局。链接目标和打开动作由调用方提供。</summary>
    internal PaperInfoButton(Font iconFont, Font bodyFont, string title, string note, string? linkText = null, Action? openLink = null,
        string? footer = null)
    {
        Text = "\uE946";
        Font = iconFont;
        AccessibleName = title;
        AccessibleDescription = footer is null ? note : $"{note} {footer}";
        Cursor = Cursors.Hand;
        ForeColor = PaperFieldSurface.Brass;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        DrawerMetrics.Bind(this, () => Size = new Size(LogicalToDeviceUnits(28), DrawerMetrics.RowHeight(this, 27, 3)));
        Anchor = AnchorStyles.Right;
        DrawerMetrics.Margin(this, 0, 0, 5, 3);
        Color surface = PaperHintBubble.Surface;
        TableLayoutPanel content = new()
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = surface, Font = bodyFont,
        };
        DrawerMetrics.Bind(content, () => content.Padding = DrawerMetrics.Space(content, 12, 9, 14, 10));
        content.Controls.Add(CreateParagraph(note), 0, 0);
        if (linkText != null && openLink != null)
        {
            _link = new LinkLabel { Text = $"{linkText}  ›", AutoSize = true, Font = bodyFont,
                LinkBehavior = LinkBehavior.HoverUnderline, LinkColor = Color.FromArgb(184, 225, 211),
                ActiveLinkColor = Color.White, AccessibleName = linkText };
            content.Controls.Add(_link, 0, 1);
            DrawerMetrics.Margin(_link, 0, 5, 0, 0);
            _link.LinkClicked += (_, _) => OpenFromBubble(openLink);
            _link.PreviewKeyDown += (_, e) => { if (e.KeyData == Keys.Enter) e.IsInputKey = true; };
            _link.KeyDown += (_, e) =>
            {
                if (e.KeyData != Keys.Enter) return;
                e.SuppressKeyPress = true;
                OpenFromBubble(openLink);
            };
        }
        if (footer != null)
        {
            Label last = CreateParagraph(footer);
            content.Controls.Add(last, 0, 2);
            DrawerMetrics.Margin(last, 0, 5, 0, 0);
        }
        _popup = new PaperHintBubble(this);
        _popup.Items.Add(new ToolStripControlHost(content) { Padding = Padding.Empty, Margin = Padding.Empty });
        _popup.Closed += (_, _) => { _leaveTimer.Stop(); _keyboardOpen = false; };
        _leaveTimer.Tick += (_, _) =>
        {
            if (!_keyboardOpen && !_popup.Bounds.Contains(MousePosition) && !ClientRectangle.Contains(PointToClient(MousePosition)))
                _popup.Close();
        };
        MouseHover += (_, _) => ShowBubble(false);
        Click += (_, _) => ShowBubble(true);
        VisibleChanged += (_, _) => { if (!Visible) _popup.Close(); };
    }

    /// <summary>限制气泡段落的行宽并保留自动增高，兼顾较大字号。</summary>
    private Label CreateParagraph(string text)
    {
        Label body = new() { Text = text, AutoSize = true, Margin = Padding.Empty, ForeColor = Color.FromArgb(249, 243, 229) };
        DrawerMetrics.Bind(body, () => body.MaximumSize = new Size(LogicalToDeviceUnits(264), 0));
        return body;
    }

    /// <summary>关闭说明并打开对应链接。键盘打开时先把焦点交还入口，便于返回后继续浏览。</summary>
    private void OpenFromBubble(Action openLink)
    {
        if (!_popup.Visible) return;
        bool restoreFocus = _keyboardOpen;
        _popup.Close();
        if (restoreFocus && CanFocus) Focus();
        openLink();
    }

    /// <summary>让气泡右缘与图标对齐。键盘激活时将焦点移给链接，鼠标悬停时保留当前操作。</summary>
    private void ShowBubble(bool focusLink)
    {
        _keyboardOpen = focusLink;
        // 悬停不取得焦点，避免菜单在弹出时因系统焦点消息立即消失。键盘打开才启用原生菜单退出行为。
        _popup.AutoClose = focusLink;
        if (!_popup.Visible)
        {
            _popup.Show(this, new Point(Width, Height), ToolStripDropDownDirection.BelowLeft);
            _popup.UpdateEdge();
        }
        if (focusLink) _link?.Focus();
        _leaveTimer.Start();
    }

    /// <summary>释放气泡、其中的链接控件以及用于延迟收起的计时器。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _popup.Close(); _leaveTimer.Dispose(); _popup.Dispose(); }
        base.Dispose(disposing);
    }

}

/// <summary>日志与环境检查共用的纸面气泡，按入口所在方向放置尖角。</summary>
internal sealed class PaperHintBubble : ToolStripDropDown
{
        internal static readonly Color Surface = Color.FromArgb(72, 64, 54);
        private readonly Control _anchor;
        private readonly bool _tipAtLeft;
        private BronzeCornerEdges.CornerWindow? _edge;
        private (Size Size, int Dpi, int Tip, Color Color) _geometry;

        /// <summary>统一气泡的底色和尖角留白，原生内容仍可响应指针和键盘。</summary>
        internal PaperHintBubble(Control anchor, bool tipAtLeft = false)
        {
            _anchor = anchor;
            _tipAtLeft = tipAtLeft;
            BackColor = Surface;
            Padding = new Padding(0, 6, 0, 0);
            DropShadowEnabled = false;
            Renderer = new ToolStripSystemRenderer();
        }

        /// <summary>按像素覆盖率拆分实体和半透明边缘。实体负责链接命中，边缘交给桌面合成器。</summary>
        internal void UpdateEdge()
        {
            if (!IsHandleCreated || !Visible || IsDisposed || Disposing || _anchor?.FindForm() is not Form owner) return;
            var geometry = (Size, DeviceDpi, Padding.Top, BackColor);
            if (_geometry == geometry && _edge != null) { _edge.Place(Location, true); return; }
            int tip = Padding.Top, radius = LogicalToDeviceUnits(8);
            if (Width < radius * 4 || Height - tip < radius * 2) return;
            using GraphicsPath shape = new(FillMode.Winding);
            shape.AddRoundedRectangle(new Rectangle(0, tip, Width, Height - tip), new Size(radius, radius));
            int x = _tipAtLeft ? Math.Max(tip + 1, _anchor.Width / 2) : Width - LogicalToDeviceUnits(14);
            shape.AddPolygon([new Point(x - tip - 1, tip + 1), new Point(x, 0), new Point(x + tip + 1, tip + 1)]);
            using Bitmap mask = new(Width, Height);
            using (var g = Graphics.FromImage(mask))
            using (var brush = new SolidBrush(BackColor))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.FillPath(brush, shape);
            }
            Region body = new();
            body.MakeEmpty();
            // 尖角和圆角的每条扫描线只有一个实心区间，仅边界像素需要交给透明层。
            for (int y = 0; y < Height; y++)
            {
                int left = 0, right = Width - 1;
                while (left <= right && mask.GetPixel(left, y).A < 255) left++;
                while (right >= left && mask.GetPixel(right, y).A < 255) right--;
                if (left <= right) body.Union(new Rectangle(left, y, right - left + 1, 1));
            }
            using (var g = Graphics.FromImage(mask))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                using SolidBrush clear = new(Color.Transparent);
                g.FillRegion(clear, body);
            }
            // 让纸面窗口拥有装饰层，避免弹出菜单的原生层级受其附属窗口影响。
            var replacement = new BronzeCornerEdges.CornerWindow(owner.Handle, mask);
            _edge?.Dispose();
            _edge = replacement;
            _geometry = geometry;
            Region = body;
            _edge.Place(Location, true);
        }

        /// <summary>弹出和关闭时同步边缘，不在图标处留下浮层。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) _edge?.Place(Point.Empty, false);
        }

        /// <summary>随浮层移动和缩放更新屏幕边缘位置。</summary>
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); UpdateEdge(); }

        /// <summary>布局改变时重建对应尺寸的透明轮廓。</summary>
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateEdge(); }

        /// <summary>随弹出层原生句柄一起释放装饰窗口。</summary>
        protected override void OnHandleDestroyed(EventArgs e)
        {
            _edge?.Dispose();
            _edge = null;
            base.OnHandleDestroyed(e);
        }

        /// <summary>填满气泡窗口形状，正文与尖角使用同一底色。</summary>
        protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(BackColor);
}
