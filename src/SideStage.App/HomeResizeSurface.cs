/*
 * 文件名称：HomeResizeSurface.cs
 * 文件职责：拖动未连接首页边框时，将标题、纸面、居中的操作区和铜框作为一张图提交给桌面。
 * 像素来源：通过 PrintWindow 读取本窗口自身的绘制结果。只复制空白纸纹，文字和按钮保持原始像素大小。
 * 生命周期：只在本次拖动中持有位图和鼠标穿透的图层。调用方负责在切换页面或结束拖动时释放。
 */
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SideStage.App;

using Dib = ResizeFrameBuffer;

internal sealed class HomeResizeSurface : NativeWindow, IDisposable
{
    private readonly Dib _original;
    private Dib? _output;
    private readonly int _header, _footer, _buttonLeft, _radius;
    private readonly Rectangle _table, _drawerBounds;
    private readonly Size _padding;
    private readonly Dib? _drawer;
    private Rectangle _last;
    private bool _shown;

    /// <summary>按当前 DPI 捕获首页。四角的半透明覆盖率与常规铜框使用同一套几何。</summary>
    internal HomeResizeSurface(Form host, Control table, int buttonLeft, Control? drawer)
    {
        _table = new(host.PointToClient(table.PointToScreen(Point.Empty)), table.Size);
        _padding = host.Padding.Size;
        _buttonLeft = buttonLeft;
        _radius = (int)Math.Round(BronzeWindowFrame.CornerRadius * host.DeviceDpi / 96d);
        _header = Math.Max(_radius, host.Padding.Top);
        _footer = Math.Max(_radius, host.Padding.Bottom);
        _original = new Dib(host.ClientSize);
        try
        {
            if (!PrintWindow(host.Handle, _original.Dc, 3)) throw new Win32Exception();
            if (drawer is not null)
            {
                // 展宽窗口可能露出原先被抽屉遮住的表格。分别保留抽屉和完整纸页，再按新位置合成。
                _drawerBounds = new(host.PointToClient(drawer.PointToScreen(Point.Empty)), drawer.Size);
                _drawer = new Dib(drawer.Size);
                _drawer.CopyFrom(_original, _drawerBounds, Point.Empty);
                _drawer.ApplyCornerAlpha(0);
                Control paper = table.Parent!;
                using Dib cleanPaper = new(paper.ClientSize);
                // WM_PRINT 让纸页和它自己的子控件直接绘制，即使其中一部分在屏幕上被同级抽屉盖住。
                const int print = 0x0317, client = 0x04, background = 0x08, children = 0x10;
                SendMessage(paper.Handle, print, cleanPaper.Dc, client | background | children);
                _original.CopyFrom(cleanPaper, new Rectangle(Point.Empty, cleanPaper.Size),
                    host.PointToClient(paper.PointToScreen(Point.Empty)));
            }
            _original.ApplyCornerAlpha(_radius);
            CreateHandle(new CreateParams { Parent = host.Handle, Style = unchecked((int)0x80000000),
                ExStyle = 0x80000 | 0x20 | 0x08000000 | 0x80 });
        }
        catch { Dispose(); throw; }
    }

    /// <summary>按真实首页的居中公式排版，标题按钮和抽屉靠右对齐，将完整像素和新窗形一次提交给桌面。</summary>
    internal bool Present(Rectangle bounds)
    {
        if (bounds == _last) return true;
        Size original = _original.Size;
        int centeredLeft = Center(_table.Left, _table.Width, _padding.Width + _drawerBounds.Width, original.Width, bounds.Width);
        int centeredTop = Center(_table.Top, _table.Height, _padding.Height, original.Height, bounds.Height);
        if (_table.Top <= _header || _table.Bottom > original.Height - _footer ||
            centeredTop < _header || centeredTop + _table.Height > bounds.Height - _footer ||
            _table.Left <= _radius || _table.Right >= original.Width - _radius ||
            centeredLeft < _radius || centeredLeft + _table.Width > bounds.Width - _radius ||
            _buttonLeft <= _radius || bounds.Width - (original.Width - _buttonLeft) <= _radius ||
            (_drawer != null && bounds.Height != original.Height)) return false;

        if (_output is null || _output.Size.Height < bounds.Height || _output.Size.Width < bounds.Width)
        {
            Dib replacement = new(new Size(Math.Max(bounds.Width + 128, _output?.Size.Width ?? 0),
                Math.Max(bounds.Height + 128, _output?.Size.Height ?? 0)));
            _output?.Dispose();
            _output = replacement;
        }
        // 标题栏的右侧三键整体移动。空白标题带仅在按钮前增减，图标与标题始终留在左边。
        int buttonStart = bounds.Width - (original.Width - _buttonLeft);
        int prefix = Math.Min(buttonStart, _buttonLeft);
        Blit(0, 0, 0, 0, prefix, _header);
        TileColumns(_buttonLeft - 1, 1, 0, prefix, buttonStart, 0, _header);
        Blit(_buttonLeft, 0, buttonStart, 0, original.Width - _buttonLeft, _header);
        FillPaper(_header, centeredTop);
        CopyBand(_table.Top, centeredTop, _table.Height);
        FillPaper(centeredTop + _table.Height, bounds.Height - _footer);
        CopyBand(original.Height - _footer, bounds.Height - _footer, _footer);
        if (_drawer != null)
            _output.CopyFrom(_drawer, new Rectangle(Point.Empty, _drawer.Size),
                new Point(_drawerBounds.X + bounds.Width - original.Width, _drawerBounds.Y));
        Point destination = bounds.Location, source = Point.Empty;
        Size size = bounds.Size;
        uint blend = 0x01ff0000;
        if (!UpdateLayeredWindow(Handle, 0, ref destination, ref size, _output.Dc, ref source, 0, ref blend, 2))
            throw new Win32Exception();
        if (!_shown) { ShowWindow(Handle, 4); _shown = true; }
        _last = bounds;
        return true;

        // 只平铺表格与标题之间已有的空白。每行自带两侧铜纹。
        void FillPaper(int y, int end)
        {
            int tile = Math.Min(96, _table.Top - _header);
            while (y < end)
            {
                int count = Math.Min(tile, end - y);
                CopyBand(_header, y, count);
                y += count;
            }
        }

        // 以表格左右端拆分纸带。保留外侧铜纹，只在表格两侧的空白处取样平铺。
        void CopyBand(int from, int to, int rows)
        {
            if (original.Width == bounds.Width) { Blit(0, from, 0, to, original.Width, rows); return; }
            int left = Math.Min(_table.Left, centeredLeft);
            int end = centeredLeft + _table.Width;
            int right = Math.Min(original.Width - _table.Right, bounds.Width - end);
            Blit(0, from, 0, to, left, rows);
            int tile = Math.Min(96, _table.Left - _radius);
            TileColumns(_table.Left - tile, tile, from, left, centeredLeft, to, rows);
            Blit(_table.Left, from, centeredLeft, to, _table.Width, rows);
            TileColumns(_table.Right, Math.Min(96, original.Width - _table.Right - _radius),
                from, end, bounds.Width - right, to, rows);
            Blit(original.Width - right, from, bounds.Width - right, to, right, rows);
        }

        // 重复已有纸纹或标题栏的纯色列，填满扩宽后新增的空间。
        void TileColumns(int sampleX, int tileWidth, int fromY, int x, int end, int y, int rows)
        {
            int span = end - x;
            if (span <= 0) return;
            int filled = Math.Min(span, tileWidth);
            Blit(sampleX, fromY, x, y, filled, rows);
            // 放宽很多时从已填区域成倍复制，纯色标题带也只需少量 GDI 调用。
            while (filled < span)
            {
                int count = Math.Min(filled, span - filled);
                _output.CopyFrom(_output, new Rectangle(x, y, count, rows), new Point(x + filled, y));
                filled += count;
            }
        }

        // 直接复制源图的像素，保留铜框透明通道和按钮的原始尺寸。
        void Blit(int sx, int sy, int dx, int dy, int width, int height) =>
            _output.CopyFrom(_original, new Rectangle(sx, sy, width, height), new Point(dx, dy));
    }

    /// <summary>按父控件的实际可用空间计算位移，保留奇数宽高时 WinForms 的整数舍入。</summary>
    private static int Center(int offset, int content, int padding, int original, int target) =>
        offset + (target - padding - content) / 2 - (original - padding - content) / 2;

    /// <summary>撤下临时图层并释放两块位图，桌面重新显示实际控件。</summary>
    public void Dispose()
    {
        if (Handle != 0) DestroyHandle();
        _output?.Dispose();
        _drawer?.Dispose();
        _original.Dispose();
    }

    /// <summary>把指定窗口自己的客户区画入目标 DC。</summary>
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    /// <summary>要求目标控件同步将自身和子控件画入指定的内存 DC。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window,
        int message, nint wParam, nint lParam);
    /// <summary>同时提交预乘像素、可见大小和屏幕位置。</summary>
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window,
        nint desktop, ref Point destination, ref Size size, nint dc, ref Point source, uint key, ref uint blend, uint flags);
    /// <summary>显示图层并保持当前键盘焦点。</summary>
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
}
