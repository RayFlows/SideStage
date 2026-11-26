/*
 * 文件名称：ParchmentPreviewForm.HomeLayout.cs
 * 文件职责：按显示器 DPI、字体实测宽度和侧栏占用空间布置首页环境检查表。
 * 排版约定：一项检查占一行。字号决定整表的行距、按钮及四周留白，长状态决定额外的行宽。
 */
namespace SideStage.App;

internal sealed partial class ParchmentPreviewForm
{
    /// <summary>绑定检查表的测量与居中布局。窗口宽度、DPI、字号、状态或操作入口改变时重新排版。</summary>
    private void ConfigureHomeLayout(PaperSurface paper, PaperSurface.BufferedTable card, Label title, FlowLayoutPanel commands)
    {
        var rows = Enumerable.Range(1, card.RowCount - 2).Select(index =>
        {
            var icon = (Label)card.GetControlFromPosition(0, index)!;
            var caption = (Label)card.GetControlFromPosition(1, index)!;
            Control result = card.GetControlFromPosition(2, index)!;
            Label value = result as Label ?? result.Controls.OfType<Label>().Single();
            return (Icon: icon, Caption: caption, Value: value, Action: result.Controls.OfType<Button>().SingleOrDefault());
        }).ToArray();
        bool arranging = false;
        int previousDpi = 0;
        bool previousWide = false;
        // GDI 的点字号相对于进程启动时的系统 DPI。换屏时 WinForms 已换算过控件字体。
        using Graphics system = Graphics.FromHwnd(IntPtr.Zero);
        float startupDpi = system.DpiY;

        card.CellPaint += (_, cell) =>
        {
            if (cell.Row < 1 || cell.Row >= card.RowCount - 1) return;
            using Pen line = new(Color.FromArgb(210, 192, 161));
            cell.Graphics.DrawLine(line, cell.CellBounds.Left, cell.CellBounds.Bottom - 1,
                cell.CellBounds.Right, cell.CellBounds.Bottom - 1);
        };

        // 表格按单行文本测量，同一行的操作按钮也计入宽度。侧栏开合只改变纸面的可视范围。
        void Arrange(bool contentChanged = false)
        {
            if (arranging || !paper.IsHandleCreated || !paper.Visible) return;
            arranging = true;
            try
            {
                // 大桌面把空间留给两列状态；普通窗口仍沿用原排版，字号和按钮不随窗口宽度跳变。
                bool wide = paper.ClientSize.Width >= paper.LogicalToDeviceUnits(1600);
                if (!contentChanged && paper.DeviceDpi == previousDpi && wide == previousWide)
                {
                    Center();
                    return;
                }
                previousDpi = paper.DeviceDpi;
                previousWide = wide;
                // 使用当前 DPI 下的字体行高与设计行高之比，让显示缩放和用户放大字号都反映到两个方向。
                double scale = Math.Max(paper.DeviceDpi / 96d,
                    startupDpi / 96d * rows.Max(r => r.Value.Font.SizeInPoints) / _bodyFont.SizeInPoints);
                int Scaled(int designPixels) => (int)Math.Round(designPixels * scale, MidpointRounding.AwayFromZero);
                card.SuspendLayout();
                try
                {
                    card.OrnamentWidth = Scaled(500);
                    card.Padding = new Padding(Scaled(40), Scaled(39), Scaled(40), Scaled(39));
                    title.MinimumSize = new Size(0, Scaled(55));
                    int commandGap = Scaled(19);
                    commands.Margin = new Padding(0, commandGap, 0, commandGap);
                    _connectButton!.MinimumSize = new Size(Scaled(138), Scaled(38));
                    _connectButton.Margin = new Padding(0, 0, Scaled(8), 0);
                    _homeSettings!.MinimumSize = new Size(Scaled(112), Scaled(38));
                    int iconWidth = Math.Max(Scaled(22), rows.Max(r =>
                        TextRenderer.MeasureText(r.Icon.Text, r.Icon.Font).Width + r.Icon.Margin.Horizontal));
                    foreach (var row in rows)
                    {
                        row.Icon.MinimumSize = new Size(iconWidth - row.Icon.Margin.Horizontal, 0);
                        row.Caption.MinimumSize = new Size(0, Scaled(39));
                        row.Caption.Margin = row.Value.Margin = new Padding(Scaled(3), 0, Scaled(3), 0);
                        if (row.Action is { } action)
                        {
                            action.MinimumSize = new Size(Scaled(100), Scaled(28));
                            action.Margin = new Padding(Scaled(9), 0, Scaled(3), 0);
                        }
                    }
                    // 按钮尺寸和各列留白已确定，再测量单行全文以免长系统名被挤到下一行。
                    int captionWidth = rows.Max(r => r.Caption.PreferredWidth + r.Caption.Margin.Horizontal);
                    int resultWidth = rows.Max(r => r.Value.PreferredWidth + r.Value.Margin.Horizontal +
                        (r.Action?.Visible == true ? r.Action.PreferredSize.Width + r.Action.Margin.Horizontal : 0));
                    int bodyWidth = Math.Max(iconWidth + captionWidth + resultWidth,
                        Math.Max(title.PreferredWidth + title.Margin.Horizontal, commands.PreferredSize.Width));
                    int width = Math.Max(Scaled(wide ? 680 : 500), card.Padding.Horizontal + bodyWidth);
                    // 先解除上一次的约束，缩小 DPI 后的首轮布局也能收回列宽。
                    Size target = new(width, 0);
                    if (card.MinimumSize != target || card.MaximumSize != target)
                    {
                        card.MinimumSize = Size.Empty;
                        card.MaximumSize = card.MinimumSize = target;
                    }
                }
                finally { card.ResumeLayout(true); }
                Center();
            }
            finally { arranging = false; }
        }

        // 可见空间充足时居中。溢出的轴沿用原生滚动位置，布局刷新也能停在用户查看的位置。
        void Center()
        {
            int top = Math.Max(0, (paper.ClientSize.Height - card.Height) / 2);
            card.Location = new Point(Math.Max(0, (paper.ClientSize.Width - card.Width) / 2) + paper.AutoScrollPosition.X,
                top + paper.AutoScrollPosition.Y);
        }

        paper.Layout += (_, _) => Arrange();
        paper.DpiChangedAfterParent += (_, _) => Arrange(true);
        // 待整个控件树完成原生缩放后，再以最终字号校正间距及按钮，避免缓存到过渡尺寸。
        DpiChanged += (_, _) => BeginInvoke(() => { if (!IsDisposed) Arrange(true); });
        paper.VisibleChanged += (_, _) => Arrange(true);
        title.FontChanged += (_, _) => Arrange(true);
        foreach (Control button in commands.Controls) button.FontChanged += (_, _) => Arrange(true);
        foreach (var row in rows)
        {
            row.Value.TextChanged += (_, _) => Arrange(true);
            row.Value.FontChanged += (_, _) => Arrange(true);
            row.Caption.FontChanged += (_, _) => Arrange(true);
            if (row.Action is not { } action) continue;
            action.VisibleChanged += (_, _) => Arrange(true);
            action.FontChanged += (_, _) => Arrange(true);
        }
    }
}
