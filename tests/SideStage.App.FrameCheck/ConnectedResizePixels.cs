/*
 * 文件名称：ConnectedResizePixels.cs
 * 文件职责：在真实桌面上比对一次往返拖动涉及的所有窗口尺寸，识别窗内漏出背板及新增的纯黑、纯白块。
 * 采样范围：包括标题、四边、预览、留边和已打开的抽屉。只在内存中保留采样结果，结束时释放。
 */
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SideStage.App.FrameCheck;

internal sealed class ConnectedResizePixels : IDisposable
{
    internal static readonly Color Backdrop = Color.FromArgb(251, 0, 215);
    private readonly Dictionary<Rectangle, byte[]> _references = [];
    private readonly Dictionary<Rectangle, int[]> _edges = [];
    private readonly Dictionary<Rectangle, int[]> _scene = [];
    private readonly Dictionary<Rectangle, bool[]> _bright = [];
    private readonly Point[] _probes;
    private readonly Rectangle _capture;
    private readonly Bitmap _bitmap;
    private readonly int[] _pixels;
    private readonly int _radius;
    private int _dragFrames, _releasedFrames, _leaks, _blocks, _releaseLeaks, _releaseBlocks;
    private string _firstBlock = string.Empty;
    private const int Pitch = 12;
    private int _missingScene;
    internal bool Passed => _dragFrames >= 2 && _releasedFrames == 8 && _leaks == 0 && _blocks == 0 && _missingScene == 0;

    /// <summary>记录各个目标尺寸在停止拖动时的底色，供高速拖动中的同尺寸画面比较。</summary>
    internal ConnectedResizePixels(Form window, Rectangle[] targets, bool checkDrawer = false)
    {
        _capture = targets.Aggregate(Rectangle.Union);
        if (!Screen.FromControl(window).Bounds.Contains(_capture))
            throw new InvalidOperationException("测试轨迹超出当前屏幕，不能采样整个窗口。");
        _bitmap = new(_capture.Width, _capture.Height, PixelFormat.Format32bppRgb);
        _pixels = new int[_capture.Width * _capture.Height];
        _radius = window.LogicalToDeviceUnits(BronzeWindowFrame.CornerRadius);
        _probes = targets.Distinct().SelectMany(EdgeProbes).Where(_capture.Contains).Distinct().ToArray();
        foreach (Rectangle target in targets.Distinct())
        {
            window.Bounds = target;
            window.Refresh();
            for (int i = 0; i < 4; i++) { Application.DoEvents(); Marshal.ThrowExceptionForHR(DwmFlush()); }
            Capture();
            _references.Add(target, Tiles(target).Select(TileColor).ToArray());
            if (checkDrawer)
                _bright.Add(target, Tiles(target).Select(p => (Pixel(p.X, p.Y) & 0x808080) == 0x808080).ToArray());
            _edges.Add(target, _probes.Select(p => Pixel(p.X, p.Y)).ToArray());
            _scene.Add(target, SceneColors(target).ToArray());
        }
        window.Bounds = targets[0];
        window.Refresh();
        Application.DoEvents();
        Marshal.ThrowExceptionForHR(DwmFlush());
    }

    /// <summary>独立于尺寸消息采样，按截图中实际出现的铜边选择静态参考，避免拿下一次鼠标位置判定上一帧。</summary>
    internal void Observe(ManualResetEventSlim ready, CancellationToken token)
    {
        try
        {
            do
            {
                Marshal.ThrowExceptionForHR(DwmFlush());
                Capture();
                Rectangle visible = _references.Keys.MinBy(EdgeDifference);
                Compare(visible);
                _dragFrames++;
                ready.Set();
            } while (!token.IsCancellationRequested);
        }
        finally { ready.Set(); }
    }

    /// <summary>松手后以应有的整个窗口为基准，即使某段外框也变透明，仍会计入背板泄漏。</summary>
    internal void CheckReleased(Rectangle expected)
    {
        int previousLeaks = _leaks, previousBlocks = _blocks;
        for (int i = 0; i < 8; i++)
        {
            Application.DoEvents();
            Marshal.ThrowExceptionForHR(DwmFlush());
            Capture();
            Compare(expected);
            _releasedFrames++;
        }
        _releaseLeaks = _leaks - previousLeaks;
        _releaseBlocks = _blocks - previousBlocks;
    }

    /// <summary>在所有候选边界的内外两侧选点，实际取色能适应不同 DPI、铜纹和桌面阴影。</summary>
    private static IEnumerable<Point> EdgeProbes(Rectangle bounds)
    {
        for (int i = 1; i < 6; i++)
        foreach (int depth in new[] { -5, 1, 4, 8 })
        {
            int x = bounds.Left + bounds.Width * i / 6, y = bounds.Top + bounds.Height * i / 6;
            yield return new(x, bounds.Top + depth);
            yield return new(x, bounds.Bottom - 1 - depth);
            yield return new(bounds.Left + depth, y);
            yield return new(bounds.Right - 1 - depth, y);
        }
    }

    /// <summary>用整圈静止参考的实测颜色识别已显示的尺寸，避免固定铜色阈值在高 DPI 下误选边界。</summary>
    private int EdgeDifference(Rectangle bounds)
    {
        int[] reference = _edges[bounds];
        int different = 0;
        for (int i = 0; i < reference.Length; i++)
            if (Pixel(_probes[i].X, _probes[i].Y) != reference[i]) different++;
        return different;
    }

    /// <summary>扫描每个窗口的内部直至靠边四个像素，圆角有意透明的区域除外。</summary>
    private IEnumerable<Point> Tiles(Rectangle r)
    {
        for (int y = r.Top + 4; y < r.Bottom - 3; y += Pitch)
        for (int x = r.Left + 4; x < r.Right - 3; x += Pitch)
        {
            if (Math.Min(x - r.Left, r.Right - x) < _radius &&
                Math.Min(y - r.Top, r.Bottom - y) < _radius) continue;
            yield return new Point(x, y);
        }
    }

    /// <summary>比较同一位置的采样块；背板始终是异常，原始远程内容中已有的黑白区域则是合法像素。</summary>
    private void Compare(Rectangle r)
    {
        int[] scene = _scene[r];
        int changed = SceneColors(r).Zip(scene).Count(pair =>
            Math.Abs((pair.First >> 16) - (pair.Second >> 16)) > 30 ||
            Math.Abs((pair.First >> 8 & 255) - (pair.Second >> 8 & 255)) > 30 ||
            Math.Abs((pair.First & 255) - (pair.Second & 255)) > 30);
        if (changed > scene.Length / 4) _missingScene++;
        byte[] reference = _references[r];
        _bright.TryGetValue(r, out bool[]? bright);
        int index = 0;
        foreach (Point p in Tiles(r))
        {
            byte color = TileColor(p);
            if (color == 3) _leaks++;
            else if ((color != 0 && color != reference[index]) || (bright?[index] == true && IsDarkPatch(p)))
            {
                if (_blocks == 0) _firstBlock = $"{color}@({p.X-r.Left},{p.Y-r.Top})";
                _blocks++;
            }
            index++;
        }
    }

    /// <summary>取抽屉以外的远程画面颜色，用本次会话的静止参考识别空白画布或错误的缩放位置。</summary>
    private IEnumerable<int> SceneColors(Rectangle r)
    {
        for (int y = 2; y < 8; y++)
        for (int x = 1; x < 6; x++)
            yield return Pixel(r.X + r.Width * x / 12, r.Y + r.Height * y / 9);
    }

    /// <summary>用中心与四角识别连续同色像素，避免把文字、图标边缘或细线误认为矩形擦除。</summary>
    private byte TileColor(Point p)
    {
        int color = Pixel(p.X, p.Y);
        if (color != 0 && color != 0xffffff && color != (Backdrop.ToArgb() & 0xffffff)) return 0;
        if (Pixel(p.X - 3, p.Y - 3) != color || Pixel(p.X + 3, p.Y - 3) != color ||
            Pixel(p.X - 3, p.Y + 3) != color || Pixel(p.X + 3, p.Y + 3) != color) return 0;
        return color == 0 ? (byte)1 : color == 0xffffff ? (byte)2 : (byte)3;
    }

    /// <summary>将屏幕点换算为本次整幅轨迹截图中的 RGB 值。</summary>
    private int Pixel(int x, int y) => _pixels[(y - _capture.Top) * _capture.Width + x - _capture.Left] & 0xffffff;

    /// <summary>针对静止参考中明亮的位置，识别由深灰色像素组成的黑斑。中心和四角都足够暗才报告。</summary>
    private bool IsDarkPatch(Point p)
    {
        const int brightBits = 0xe0e0e0;
        return (brightBits & (Pixel(p.X, p.Y) | Pixel(p.X - 3, p.Y - 3) | Pixel(p.X + 3, p.Y - 3) |
            Pixel(p.X - 3, p.Y + 3) | Pixel(p.X + 3, p.Y + 3))) == 0;
    }

    /// <summary>复用同一缓冲区读取桌面合成结果，避免逐像素调用 GDI 或将远程桌面写入磁盘。</summary>
    private void Capture()
    {
        using (Graphics graphics = Graphics.FromImage(_bitmap))
            graphics.CopyFromScreen(_capture.Location, Point.Empty, _bitmap.Size);
        BitmapData data = _bitmap.LockBits(new Rectangle(Point.Empty, _bitmap.Size),
            ImageLockMode.ReadOnly, _bitmap.PixelFormat);
        try { Marshal.Copy(data.Scan0, _pixels, 0, _pixels.Length); }
        finally { _bitmap.UnlockBits(data); }
    }

    /// <summary>输出本次拖动与释放后的采样数量及异常类型。</summary>
    public override string ToString() => $"拖动/松手 {_dragFrames}/{_releasedFrames} 帧，" +
        $"透底 {_leaks - _releaseLeaks}/{_releaseLeaks} 块，黑白 {_blocks - _releaseBlocks}/{_releaseBlocks} 块 {_firstBlock}，画面失配 {_missingScene} 帧";
    /// <summary>释放用于桌面采样的 GDI 位图。</summary>
    public void Dispose() => _bitmap.Dispose();
    /// <summary>等待桌面完成一次合成。</summary>
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
