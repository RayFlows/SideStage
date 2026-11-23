/*
 * 文件名称：BronzeCornerEdges.cs
 * 文件职责：为顶层窗口的四段外圆弧补上逐像素透明度。原生窗口区域负责完全覆盖的像素，
 *   四个小型分层窗口负责与桌面相交的边缘像素，纵向拖动时用一层缓存显示底部直边。
 * 生命周期：边缘随宿主句柄和 DPI 创建、释放。纯纵向拉伸只平移已上传的铜纹。
 * 输入约定：边缘窗口归宿主所有，鼠标穿透，不能取得焦点或出现在任务栏。
 */
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SideStage.App;

internal sealed class BronzeCornerEdges : IDisposable
{
    private readonly CornerWindow[] _windows = new CornerWindow[4];
    private readonly Region _cutout = new();
    private readonly int _radius;
    private readonly float _scale;
    private readonly nint _owner;
    private CornerWindow? _bottom;
    private int _paperDepth;
    private int _bottomWidth;
    internal bool BottomActive { get; private set; }

    /// <summary>按照像素方格与圆弧的交集生成裁切行和透明图，创建中途失败也释放已取得的句柄。</summary>
    internal BronzeCornerEdges(nint owner, float scale)
    {
        _scale = scale;
        int radius = _radius = (int)Math.Round(BronzeWindowFrame.CornerRadius * scale);
        _owner = owner;
        _cutout.MakeEmpty();
        using Bitmap mask = new(radius, radius, PixelFormat.Format32bppArgb);
        for (int y = 0; y < radius; y++)
        {
            int dy = radius - y;
            int cut = (int)Math.Ceiling(radius - Math.Sqrt(radius * radius - dy * dy));
            if (cut > 0) _cutout.Union(new Rectangle(0, y, cut, 1));
            for (int x = 0; x < cut; x++)
                mask.SetPixel(x, y, Color.FromArgb(Coverage(x, y, radius), BronzeWindowFrame.OuterInk));
        }
        try
        {
            for (int i = 0; i < _windows.Length; i++)
            {
                using Bitmap oriented = (Bitmap)mask.Clone();
                if ((i & 1) != 0) oriented.RotateFlip(RotateFlipType.RotateNoneFlipX);
                if ((i & 2) != 0) oriented.RotateFlip(RotateFlipType.RotateNoneFlipY);
                _windows[i] = new CornerWindow(owner, oriented);
            }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>用 16×16 个子像素估计弧线覆盖率。该计算仅在圆角像素半径改变时执行。</summary>
    internal static int Coverage(int x, int y, int radius)
    {
        const int samples = 16;
        int covered = 0;
        for (int sy = 0; sy < samples; sy++)
        {
            double dy = radius - y - (sy + .5) / samples;
            for (int sx = 0; sx < samples; sx++)
            {
                double dx = radius - x - (sx + .5) / samples;
                if (dx * dx + dy * dy <= radius * radius) covered++;
            }
        }
        return (covered * 255 + samples * samples / 2) / (samples * samples);
    }

    /// <summary>返回只含完全覆盖像素的宿主区域。每条扫描线留下的位置由对应角的透明图填充。</summary>
    internal Region CreateSolidRegion(Size size)
    {
        Region region = new(new Rectangle(Point.Empty, size));
        for (int corner = 0; corner < 4; corner++)
        {
            bool right = (corner & 1) != 0, lower = (corner & 2) != 0;
            using Region cutout = _cutout.Clone();
            using System.Drawing.Drawing2D.Matrix mirror = new(right ? -1 : 1, 0, 0, lower ? -1 : 1,
                right ? size.Width : 0, lower ? size.Height : 0);
            cutout.Transform(mirror);
            region.Exclude(cutout);
        }
        return region;
    }

    /// <summary>
    /// 更新宿主裁切形状，最大化时恢复矩形。主窗口的尺寸消息已经安排重绘，
    /// 连续拖动时仅提交几何。切换最大化或 DPI 时请求系统重新绘制整个窗口。成功后 HRGN 归 Windows 所有。
    /// </summary>
    internal void ApplySolidRegion(nint owner, Size size, bool maximized, bool fullRedraw)
    {
        nint handle = 0;
        if (!maximized)
        {
            using Region region = CreateSolidRegion(size);
            using Graphics context = Graphics.FromHwnd(owner);
            handle = region.GetHrgn(context);
        }
        if (SetWindowRgn(owner, handle, fullRedraw) != 0) return;
        if (handle != 0) DeleteObject(handle);
        throw new InvalidOperationException("无法更新预览窗口的圆角裁切区域。");
    }

    /// <summary>按宿主屏幕坐标放置边缘。宽度改变时交回常规绘制，避免横向拖动反复上传底边位图。</summary>
    internal void Place(Rectangle bounds, bool visible)
    {
        if (BottomActive)
        {
            if (visible && bounds.Width == _bottomWidth)
            {
                _bottom!.Place(BottomOrigin(bounds), true);
            }
            else
            {
                HideResizeRim();
            }
        }
        for (int i = 0; i < _windows.Length; i++)
        {
            Point anchor = new((i & 1) == 0 ? bounds.Left : bounds.Right - _radius,
                (i & 2) == 0 ? bounds.Top : bounds.Bottom - _radius);
            _windows[i].Place(anchor, visible);
        }
    }

    /// <summary>
    /// 首次纵向缩放时上传底部直边。首页取空白纸缘与铜条组成同一张图，避免两次屏幕合成之间露出背板。
    /// 设置页和实时预览传入零纸缘，此时装饰只占用窗框自身的像素。
    /// </summary>
    internal void PlaceVerticalResizeRim(Rectangle bounds, PaperSurface? paper, int paperDepth)
    {
        int width = bounds.Width - 2 * _radius;
        int band = BronzeWindowFrame.ContentInset(_scale, false);
        if (width <= 0 || bounds.Height <= band) return;
        if (_bottom == null || bounds.Width != _bottomWidth || paperDepth != _paperDepth)
        {
            using Bitmap pixels = new(width, band + paperDepth);
            using (Graphics g = Graphics.FromImage(pixels))
            {
                g.Clear(BronzeWindowFrame.OuterInk);
                g.TranslateTransform(-_radius, pixels.Height - bounds.Height);
                BronzeWindowFrame.Paint(g, bounds.Size, _scale, false, Rectangle.Empty, 0, Color.Empty);
                g.ResetTransform();
                paper?.PaintBlankMargin(g, new Rectangle(0, 0, width, paperDepth));
            }
            CornerWindow replacement = new(_owner, pixels);
            _bottom?.Dispose();
            _bottom = replacement;
            _bottomWidth = bounds.Width;
            _paperDepth = paperDepth;
        }
        _bottom.Place(BottomOrigin(bounds), true);
        BottomActive = true;
    }

    /// <summary>直边从左下圆弧的切点开始，位图底部与宿主底部对齐。</summary>
    private Point BottomOrigin(Rectangle bounds) =>
        new(bounds.Left + _radius, bounds.Bottom - BronzeWindowFrame.ContentInset(_scale, false) - _paperDepth);

    /// <summary>结束拖动或切换页面时撤下临时铜条，宿主恢复绘制完整的原始窗框。</summary>
    internal void HideResizeRim()
    {
        _bottom?.Place(Point.Empty, false);
        BottomActive = false;
    }

    /// <summary>随宿主句柄释放全部边缘窗口。</summary>
    public void Dispose()
    {
        _bottom?.Dispose();
        foreach (CornerWindow? window in _windows) window?.Dispose();
        _cutout.Dispose();
    }

    /// <summary>持有一份小型预乘透明图的原生弹出窗口。</summary>
    internal sealed class CornerWindow : NativeWindow, IDisposable
    {
        private Point? _position;
        private bool _visible;

        /// <summary>创建不可激活的鼠标穿透窗口，并将透明图一次性交给桌面合成器。</summary>
        internal CornerWindow(nint owner, Bitmap mask)
        {
            const int layered = 0x80000, transparent = 0x20, noActivate = 0x8000000, toolWindow = 0x80;
            CreateHandle(new CreateParams { Parent = owner, Style = unchecked((int)0x80000000),
                ExStyle = layered | transparent | noActivate | toolWindow, Width = mask.Width, Height = mask.Height });
            try { Upload(Handle, mask); }
            catch { DestroyHandle(); throw; }
        }

        /// <summary>上传含预乘透明通道的 BGRA 位图，临时内存 DC 及位图在上传结束后即可释放。</summary>
        internal static void Upload(nint window, Bitmap mask)
        {
            nint dc = CreateCompatibleDC(0);
            if (dc == 0) throw new Win32Exception();
            nint bitmap = 0, previous = 0;
            try
            {
                bitmap = mask.GetHbitmap(Color.FromArgb(0));
                previous = SelectObject(dc, bitmap);
                Size size = mask.Size;
                Point origin = Point.Empty;
                // BLENDFUNCTION 的四个字节依次为源上叠加、零标志、全局不透明度、使用逐像素透明度。
                uint blend = 0x01ff0000;
                if (!UpdateLayeredWindow(window, 0, 0, ref size, dc, ref origin, 0, ref blend, 2))
                    throw new Win32Exception();
            }
            finally
            {
                if (previous != 0) SelectObject(dc, previous);
                if (bitmap != 0) DeleteObject(bitmap);
                DeleteDC(dc);
            }
        }

        /// <summary>更新屏幕位置和可见状态，保留宿主的焦点及层叠顺序。</summary>
        internal void Place(Point position, bool visible)
        {
            if (_visible == visible && (!visible || _position == position)) return;
            const uint noSize = 1, noMove = 2, noZOrder = 4, noActivate = 16, show = 64, hide = 128;
            uint flags = noSize | noActivate | noZOrder;
            if (!visible || _position == position) flags |= noMove;
            if (_visible != visible) flags |= visible ? show : hide;
            if (SetWindowPos(Handle, 0, position.X, position.Y, 0, 0, flags))
            {
                if (visible) _position = position;
                _visible = visible;
            }
        }

        /// <summary>关闭角窗并释放其桌面合成位图。</summary>
        public void Dispose() { if (Handle != 0) DestroyHandle(); }
    }

    /// <summary>创建兼容内存设备上下文用于上传像素。</summary>
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    /// <summary>选入位图并返回此前的 GDI 对象。</summary>
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint bitmap);
    /// <summary>释放临时位图。</summary>
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint bitmap);
    /// <summary>释放内存设备上下文。</summary>
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    /// <summary>向分层窗口上传像素和混合参数。</summary>
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window,
        nint destDc, nint dest, ref Size size, nint srcDc, ref Point src, uint key, ref uint blend, uint flags);
    /// <summary>更新角窗在桌面上的位置或可见性。</summary>
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after,
        int x, int y, int width, int height, uint flags);
    /// <summary>设置顶层窗口的可见区域，redraw 控制是否额外立即重绘受影响的窗口。</summary>
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
}
