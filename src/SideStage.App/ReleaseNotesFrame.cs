/*
 * 文件职责：仅承载发行说明正文，内容溢出时在浅金细框内滚动；小标题由更新窗口排版。
 * 排版约定：正文以滚动面板的可见宽度为上限自动折行；只在高度不足时纵向滚动。
 * 绘制约定：仅使用一层浅纸色和一条抗锯齿细线，不使用凹陷边缘或系统滚动条底框。
 */
using System.Drawing.Drawing2D;

namespace SideStage.App;

internal sealed class ReleaseNotesFrame : Panel
{
    /// <summary>将发行版正文作为纯文本显示；宽度随视口变化，长说明沿用设置页的细滚动条。</summary>
    internal ReleaseNotesFrame(string notes)
    {
        DoubleBuffered = ResizeRedraw = true;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(255, 251, 242);
        ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : PaperFieldSurface.Ink;
        Dock = DockStyle.Fill;
        var surface = new Panel { AutoScroll = true, BackColor = BackColor };
        var page = new PaperScrollPage(surface) { BackColor = BackColor };
        var body = new Label { Text = string.IsNullOrWhiteSpace(notes) ? "此版本未提供更新说明。" : notes,
            Dock = DockStyle.Top, AutoSize = true, UseMnemonic = false };
        /// <summary>自动滚动面板允许内容超宽；用实际客户区限制正文，当窗口或滑块占位改变时重新折行。</summary>
        void FitBodyWidth()
        {
            if (surface.ClientSize.Width > 0) body.MaximumSize = new Size(surface.ClientSize.Width, 0);
        }
        surface.ClientSizeChanged += (_, _) => FitBodyWidth();
        FitBodyWidth();
        page.SetPageContent(body);
        Controls.Add(page);
        DrawerMetrics.Bind(this, () => Padding = DrawerMetrics.Space(this, 14, 10, 14, 10));
    }

    /// <summary>底纸和边线分别绘制，内缩半个线宽，避免圆角被子窗口边界截断。</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (Width < 3 || Height < 3) return;
        float unit = DeviceDpi / 96f;
        var bounds = new RectangleF(unit / 2, unit / 2, Width - unit, Height - unit);
        float radius = Math.Min(7 * unit, Math.Min(bounds.Width, bounds.Height) / 2);
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(bounds, new SizeF(radius, radius));
        using var paper = new SolidBrush(BackColor);
        using var edge = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(216, 201, 174), unit);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(paper, path);
        g.DrawPath(edge, path);
    }
}
