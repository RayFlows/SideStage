/*
 * 文件名称：PaperSurface.cs
 * 文件职责：绘制未连接首页的米色纸纹和浅金内框。
 * 资源边界：纸纹由代码生成。外框由顶层窗体独立绘制。
 * 布局边界：纸面与远程画面使用同一个框内矩形，切换页面时尺寸保持一致。
 */

using System.Drawing.Drawing2D;

namespace SideStage.App;

/// <summary>采用可平铺细纹的原创纸面，作为未连接时的背景。</summary>
internal sealed class PaperSurface : Panel
{
    private readonly Bitmap _grain = CreateGrain();
    private readonly TextureBrush _paper;

    /// <summary>把中央透明表格的背景和文字合并绘制一次，避免移动表格时同步擦除纸面。</summary>
    internal sealed class BufferedTable : TableLayoutPanel
    {
        private readonly Svg.SvgDocument _frame;
        private Bitmap? _raster;

        /// <summary>仅缓冲首页的小表，不干预原生远程桌面子窗口的绘制。</summary>
        internal BufferedTable()
        {
            DoubleBuffered = true;
            using Stream stream = typeof(PaperSurface).Assembly.GetManifestResourceStream("SideStage.App.GiltInnerFrame.svg")
                ?? throw new InvalidDataException("缺少首页浅金内框。");
            _frame = Svg.SvgDocument.Open<Svg.SvgDocument>(stream);
        }

        /// <summary>按表格宽度重新采样矢量素材。上下方角和菱纹等比绘制，仅调整中段直线的长度。</summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            if (Width < 8 || Height < 8) return;
            if (_raster?.Width != Width)
            {
                _raster?.Dispose();
                _raster = _frame.Draw(Width, 0);
            }
            // 源图上下各 100 个单位包含全部角花，中段只有左右两条金线。
            int cap = Math.Min(Height / 2, (int)Math.Ceiling(_raster.Width * 100 / _frame.ViewBox.Width));
            int middle = Height - cap * 2;
            e.Graphics.DrawImage(_raster, new Rectangle(0, 0, Width, cap), 0, 0, Width, cap, GraphicsUnit.Pixel);
            if (middle > 0) e.Graphics.DrawImage(_raster, new Rectangle(0, cap, Width, middle),
                0, cap, Width, _raster.Height - 2 * cap, GraphicsUnit.Pixel);
            e.Graphics.DrawImage(_raster, new Rectangle(0, Height - cap, Width, cap),
                0, _raster.Height - cap, Width, cap, GraphicsUnit.Pixel);
        }

        /// <summary>在首页释放时归还当前 DPI 下的内框位图。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) _raster?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>使透明子控件能透出同一张纸纹，并在调整窗口时刷新。</summary>
    public PaperSurface()
    {
        _paper = new TextureBrush(_grain);
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(244, 238, 224);
    }

    /// <summary>生成可重复的细颗粒纸纹，小块纹理在每次重绘时沿两个轴平铺。</summary>
    private static Bitmap CreateGrain()
    {
        Bitmap tile = new(96, 96);
        Random random = new(574);
        for (int y = 0; y < tile.Height; y++)
        {
            for (int x = 0; x < tile.Width; x++)
            {
                int tone = random.Next(-4, 4);
                tile.SetPixel(x, y, Color.FromArgb(244 + tone, 238 + tone, 224 + tone));
            }
        }
        return tile;
    }

    /// <summary>复用随页面持有的纹理画刷，仅平铺本次失效区域。透明子控件也会借用这一绘制过程。</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        PaintBlankMargin(e.Graphics, e.ClipRectangle);
    }

    /// <summary>用首页同一份纸纹填充无控件的区域，供页面重绘和拖动期间的临时纸缘共用。</summary>
    internal void PaintBlankMargin(Graphics graphics, Rectangle area) => graphics.FillRectangle(_paper, area);

    /// <summary>随纸面控件释放平铺位图使用的 GDI 资源。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _paper.Dispose();
            _grain.Dispose();
        }
        base.Dispose(disposing);
    }
}
