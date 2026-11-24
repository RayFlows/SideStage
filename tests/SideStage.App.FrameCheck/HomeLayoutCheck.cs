/*
 * 文件名称：HomeLayoutCheck.cs
 * 文件职责：在实际的首页控件上检查大字、长状态、首次配置按钮和侧栏合用空间时的排版。
 * 测试边界：只更改测试窗口的字号和文字，不更改桌面缩放，不发起 RDP 连接。
 */
using SideStage.App;

namespace SideStage.App.FrameCheck;

internal static class HomeLayoutCheck
{
    /// <summary>依次放大首页文字，核对宽窗和最小窗口侧栏打开时每项文字、按钮的实际可用面积。</summary>
    internal static void Run(ParchmentPreviewForm window, string? screenshot)
    {
        PaperSurface paper = Descendants(window).OfType<PaperSurface>().Single();
        Control[] fields = Descendants(paper).Where(c => c is Label or Button).ToArray();
        var originals = fields.ToDictionary(c => c, c => c.Font);
        Button advanced = fields.OfType<Button>().Single(b => b.Text == "高级设置");
        Label os = fields.OfType<Label>().Single(l => l.AccessibleName == "操作系统状态");
        os.Text = "Windows 11 Enterprise LTSC Evaluation 专业工作站版（测试）";
        Rectangle earlier = Rectangle.Empty;
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            var fonts = originals.Values.Distinct().ToDictionary(f => f,
                f => new Font(f.FontFamily, f.SizeInPoints * scale, f.Style, GraphicsUnit.Point));
            try
            {
                foreach (Control field in fields) field.Font = fonts[originals[field]];
                Rectangle work = Screen.FromControl(window).WorkingArea;
                Size size = new(Math.Min(1500, work.Width), Math.Min(1000, work.Height));
                window.Bounds = new Rectangle(work.Left + (work.Width - size.Width) / 2,
                    work.Top + (work.Height - size.Height) / 2, size.Width, size.Height);
                CheckFields(fields, paper);
                Rectangle frame = paper.Controls[0].Bounds;
                if (frame.Width <= paper.ClientSize.Width && frame.Height <= paper.ClientSize.Height &&
                    !earlier.IsEmpty && (frame.Left >= earlier.Left || frame.Right <= earlier.Right ||
                    frame.Top >= earlier.Top || frame.Bottom <= earlier.Bottom))
                    throw new InvalidOperationException($"字号增大时检查框须从中心向四边展开：{earlier} -> {frame}。");
                earlier = frame;
                Console.WriteLine($"字号 {scale:P0}，检查框 {frame.Width} × {frame.Height}。");
                if (scale == 2 && screenshot != null)
                {
                    string stressName = os.Text;
                    os.Text = "Windows 11 专业工作站版";
                    SettingsInteractionCheck.Capture(window, screenshot);
                    os.Text = stressName;
                }
                window.Size = window.MinimumSize;
                advanced.PerformClick();
                CheckFields(fields, paper);
                if (paper.Controls[0].Width > paper.ClientSize.Width)
                {
                    if (!paper.HorizontalScroll.Visible) throw new InvalidOperationException("窄窗无法横向查看整行。");
                    paper.AutoScrollPosition = new Point(paper.DisplayRectangle.Width, 0);
                    paper.PerformLayout();
                    if (paper.AutoScrollPosition.X >= 0 || paper.Controls[0].Right > paper.ClientSize.Width)
                        throw new InvalidOperationException("滚动后仍未显示检查框最右端。");
                    paper.AutoScrollPosition = Point.Empty;
                }
                if (paper.VerticalScroll.Visible)
                {
                    paper.ScrollControlIntoView(advanced);
                    Rectangle area = paper.RectangleToClient(advanced.RectangleToScreen(advanced.ClientRectangle));
                    if (!paper.ClientRectangle.Contains(area))
                        throw new InvalidOperationException($"滚动后底部按钮仍不可见：{area}。");
                    paper.AutoScrollPosition = Point.Empty;
                }
                Descendants(window).OfType<Button>().Single(b => b.AccessibleName == "收起侧栏").PerformClick();
                Console.WriteLine($"首页字号 {scale:P0}，宽窗及最小窗口侧栏布局通过。");
            }
            finally
            {
                foreach (Control field in fields) field.Font = originals[field];
                foreach (Font font in fonts.Values) font.Dispose();
            }
        }
    }

    /// <summary>测量单行全文，核对字段、状态和操作共用同一行，并验证宽度足够时完整可见。</summary>
    private static void CheckFields(Control[] fields, PaperSurface paper)
    {
        Application.DoEvents();
        var occupied = new List<Rectangle>();
        foreach (Control field in fields.Where(c => c.Visible && c.Text.Length > 0))
        {
            Size required = field is Label label ? label.GetPreferredSize(Size.Empty)
                : TextRenderer.MeasureText(field.Text, field.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            Rectangle area = paper.RectangleToClient(field.RectangleToScreen(field.ClientRectangle));
            if (field.Width < required.Width || field.Height < required.Height ||
                !paper.Controls[0].Bounds.Contains(area) || occupied.Any(area.IntersectsWith))
                throw new InvalidOperationException($"首页「{field.Text}」区域 {area}，文本需要 {required}，纸面 {paper.ClientSize}。");
            occupied.Add(area);
        }
        foreach (Label value in fields.OfType<Label>().Where(l => l.AccessibleName?.EndsWith("状态") == true))
        {
            Label caption = fields.OfType<Label>().Single(l => l.Text + "状态" == value.AccessibleName);
            int middle = value.PointToScreen(new Point(0, value.Height / 2)).Y;
            foreach (Control sibling in value.Parent!.Controls.OfType<Button>().Where(b => b.Visible).Cast<Control>().Append(caption))
            {
                Rectangle area = sibling.RectangleToScreen(sibling.ClientRectangle);
                if (middle < area.Top || middle >= area.Bottom)
                    throw new InvalidOperationException($"「{sibling.Text}」与「{value.Text}」跨行。");
            }
        }
    }

    /// <summary>遍历真实控件树，兼容首页按窗口宽度重新排列字段和按钮。</summary>
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}
