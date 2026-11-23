/*
 * 文件名称：BronzeWindowFrame.cs
 * 文件职责：根据参考图的边缘取样，绘制棕黑外线、受光铜面与内侧暗底。
 * 几何约定：窗口裁切和铜边共用同一条外轮廓。所有宽度以 96 DPI 下的逻辑像素表示。
 * 绘制边界：四条边和转角属于同一个闭合图形，窗口拉伸只改变直边长度。
 */

using System.Drawing.Drawing2D;

namespace SideStage.App;

/// <summary>提供古铜外框的轮廓和颜色，供验收窗体绘制与裁切使用。</summary>
internal static class BronzeWindowFrame
{
    internal const int CornerRadius = 27;
    internal const int RimWidth = 8;
    internal static readonly Color OuterInk = Color.FromArgb(85, 69, 54);
    private static readonly Color Bronze = Color.FromArgb(145, 125, 100);
    private static readonly Color Highlight = Color.FromArgb(161, 139, 112);
    private static readonly Color BevelShade = Color.FromArgb(106, 85, 64);
    private static readonly Color Recess = Color.FromArgb(83, 66, 48);

    /// <summary>计算能完整放进内圆弧的矩形留边，保证远程画面四个角都可见、可点击。</summary>
    internal static int ContentInset(float scale, bool maximized)
    {
        // 在 45 度方向，圆弧到其外接正方形角点的单轴距离为 r × (1 − 1/√2)。
        double clearance = maximized ? 0 : (CornerRadius - RimWidth) * (1 - 1 / Math.Sqrt(2));
        return (int)Math.Ceiling((RimWidth + clearance) * scale);
    }

    /// <summary>建立一条闭合的像素坐标路径；最大化时半径为零，沿显示器工作区直角收口。</summary>
    internal static GraphicsPath Outline(RectangleF bounds, float radius)
    {
        GraphicsPath path = new();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>按照来自上方的光线绘制铜条截面，外上沿和内下沿受光，左右两侧保留铜本色。</summary>
    internal static void Paint(Graphics graphics, Size size, float scale, bool maximized,
        Rectangle workspace, int captionBottom, Color captionColor)
    {
        if (Math.Min(size.Width, size.Height) <= 2 * RimWidth * scale) return;
        GraphicsState state = graphics.Save();
        try
        {
            // 中央矩形另有原生子窗口，拖边重绘时只需给实际露出的边框着色。
            if (!workspace.IsEmpty) graphics.ExcludeClip(workspace);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float radius = maximized ? 0 : CornerRadius;
            FillBand(BevelShade, 2, 2, 2);
            FillBand(Highlight, 2, 2, 3.5f);
            FillBand(Bronze, 2, 3.5f, 4);
            FillBand(BevelShade, 7, 6, 6);
            FillBand(Highlight, 7.5f, 8, 6);
            FillBand(Recess, RimWidth, RimWidth, RimWidth);
            graphics.SetClip(new Rectangle(0, 0, size.Width, captionBottom), CombineMode.Intersect);
            FillBand(captionColor, RimWidth, RimWidth, RimWidth);

            // 横向内缩固定铜条厚度；上、下内缩的差值决定铜面哪一侧露出窄高光。
            void FillBand(Color color, float side, float top, float bottom)
            {
                RectangleF bounds = RectangleF.FromLTRB(side * scale, top * scale,
                    size.Width - side * scale, size.Height - bottom * scale);
                using GraphicsPath path = Outline(bounds, Math.Max(0, radius - side) * scale);
                using SolidBrush brush = new(color);
                graphics.FillPath(brush, path);
            }
        }
        finally { graphics.Restore(state); }
    }
}
