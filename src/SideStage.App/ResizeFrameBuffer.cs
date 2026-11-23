/*
 * 文件名称：ResizeFrameBuffer.cs
 * 文件职责：为首页及连接预览的拖动画面提供共享的 32 位 GDI 内存位图。
 * 布局约定：设备像素自顶向下存储。容量可大于正在显示的窗口，圆角始终按实际可见尺寸计算。
 * 生命周期：每块位图归一个拖动显示层所有，临时像素只在内存中使用。
 */
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SideStage.App;

internal sealed class ResizeFrameBuffer : IDisposable
{
    internal readonly Size Size;
    internal readonly nint Dc;
    private readonly nint _bitmap, _previous, _pixels;
    private int[]? _scratch;
    private int[] _corner = [];

    /// <summary>创建共用的 GDI 设备上下文和位图，初始化中途失败时释放已取得的句柄。</summary>
    internal ResizeFrameBuffer(Size size)
    {
        Size = size;
        Dc = CreateCompatibleDC(0);
        if (Dc == 0) throw new Win32Exception();
        BitmapHeader header = new() { Length = 40, Width = size.Width, Height = -size.Height, Planes = 1, Bits = 32 };
        _bitmap = CreateDIBSection(Dc, ref header, 0, out _pixels, 0, 0);
        if (_bitmap != 0) _previous = SelectObject(Dc, _bitmap);
        if (_previous == 0 || _previous == -1) { Dispose(); throw new Win32Exception(); }
    }

    /// <summary>将源图中的矩形复制到当前缓冲区，保留原始颜色与透明度。</summary>
    internal void CopyFrom(ResizeFrameBuffer source, Rectangle area, Point dest)
    {
        if (area.Width > 0 && area.Height > 0 && !BitBlt(Dc, dest.X, dest.Y, area.Width, area.Height,
            source.Dc, area.X, area.Y, 0x00CC0020)) throw new Win32Exception();
    }

    /// <summary>将 GDI 绘制的内部像素设为不透明，对齐四角的预乘透明像素。连续拖动复用扫描缓冲及圆弧采样。</summary>
    internal void ApplyCornerAlpha(int radius, Size? visible = null)
    {
        Size view = visible ?? Size;
        int count = checked(Size.Width * view.Height);
        int[] pixels = _scratch ??= new int[checked(Size.Width * Size.Height)];
        GdiFlush();
        Marshal.Copy(_pixels, pixels, 0, count);
        for (int i = 0; i < count; i++) pixels[i] |= unchecked((int)0xff000000);
        if (_corner.Length != radius * radius) _corner = CreateCorner(radius);
        for (int y = 0; y < radius; y++)
        for (int x = 0; x < radius; x++)
        {
            int pixel = _corner[y * radius + x];
            if ((uint)pixel >> 24 == 255) continue;
            int row = y * Size.Width, opposite = (view.Height - 1 - y) * Size.Width;
            pixels[row + x] = pixels[opposite + x] = pixel;
            pixels[row + view.Width - 1 - x] = pixels[opposite + view.Width - 1 - x] = pixel;
        }
        Marshal.Copy(pixels, 0, _pixels, count);
    }

    /// <summary>对单个左上圆角计算覆盖率并预乘铜色，其余三角共用镜像后的像素。</summary>
    private static int[] CreateCorner(int radius)
    {
        int[] corner = new int[radius * radius];
        Color ink = BronzeWindowFrame.OuterInk;
        for (int y = 0; y < radius; y++)
        for (int x = 0; x < radius; x++)
        {
            int a = BronzeCornerEdges.Coverage(x, y, radius);
            corner[y * radius + x] = a << 24 | (ink.R * a / 255) << 16 | (ink.G * a / 255) << 8 | ink.B * a / 255;
        }
        return corner;
    }

    /// <summary>先还原之前的 GDI 对象，再释放位图及内存设备上下文。</summary>
    public void Dispose()
    {
        if (_previous != 0 && _previous != -1) SelectObject(Dc, _previous);
        if (_bitmap != 0) DeleteObject(_bitmap);
        if (Dc != 0) DeleteDC(Dc);
    }

    /// <summary>与 Windows 32 位无压缩 DIB 的位图信息头字段排列一致。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Length;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int HorizontalResolution, VerticalResolution;
        public uint Colors, ImportantColors;
    }

    /// <summary>在内存位图之间复制矩形像素。</summary>
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint dest, int x, int y, int w,
        int h, nint source, int sx, int sy, uint operation);
    /// <summary>创建内存绘图上下文。</summary>
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint context);
    /// <summary>分配可直接读写像素的位图。</summary>
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc,
        ref BitmapHeader info, uint usage, out nint bits, nint section, uint offset);
    /// <summary>将位图选入设备上下文，返回此前选中的对象。</summary>
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    /// <summary>等待本线程完成 GDI 绘制，之后可直接访问位图内存。</summary>
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    /// <summary>释放位图。</summary>
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    /// <summary>释放设备上下文。</summary>
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
