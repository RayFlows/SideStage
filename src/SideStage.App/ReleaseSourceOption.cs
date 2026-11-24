/*
 * 文件职责：绘制更新窗口的下载源选项。图标识别平台，铜色对勾表示已选，二者含义独立。
 * 输入约定：保留 WinForms 单选框的互斥、方向键、空格和辅助功能语义，整块区域均可点击。
 * 缩放约定：按当前 DPI 从嵌入的 SVG 采样，并按文字、图标和对勾的实际宽度决定布局。
 */
using System.Drawing.Drawing2D;
using Svg;

namespace SideStage.App;

internal sealed class ReleaseSourceOption : RadioButton
{
    private const TextFormatFlags Type = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
        TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
    private readonly string _brand;
    private readonly Font _hintFont, _checkFont = new("Segoe MDL2 Assets", 11);
    private readonly SvgDocument _symbol;
    private Bitmap? _raster;
    private bool _hover;
    internal UpdateSource Source { get; }

    /// <summary>只在本地加载对应标识，不向平台发起图标请求；读屏名称也包含「国内推荐」。</summary>
    internal ReleaseSourceOption(UpdateSource source, Font hintFont)
    {
        Source = source;
        _hintFont = hintFont;
        _brand = source.Domestic ? "CNB" : "GitHub";
        Text = AccessibleName = source.Domestic ? "CNB（国内推荐）" : _brand;
        string resource = $"SideStage.App.UpdateSources.{(source.Domestic ? "Cnb" : "GitHub")}.svg";
        using var stream = typeof(ReleaseSourceOption).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"缺少下载源图标：{_brand}");
        _symbol = SvgDocument.Open<SvgDocument>(stream);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        MouseCaptureChanged += (_, _) => Invalidate();
        CheckedChanged += (_, _) => Invalidate();
    }

    /// <summary>为两端留白、平台图标和选中标记预留固定位置，切换选中项不会让标签移动。</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        int label = TextRenderer.MeasureText(_brand, Font, Size.Empty, Type).Width;
        int hint = Source?.Domestic == true
            ? LogicalToDeviceUnits(8) + TextRenderer.MeasureText("国内推荐", _hintFont, Size.Empty, Type).Width : 0;
        return new(LogicalToDeviceUnits(12 + 21 + 8 + 10 + 18 + 12) + label + hint, DrawerMetrics.RowHeight(this, 39, 12));
    }

    /// <summary>只填充圆角底色，不绘制按钮轮廓。对勾指示选中，名称下划线指示键盘焦点，在高对比度下也能区分两者。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 4 || Height < 4) return;
        var g = e.Graphics;
        bool high = SystemInformation.HighContrast, focus = Focused && ShowFocusCues, hover = _hover && Enabled;
        Color ink = high && Checked ? SystemColors.HighlightText : high ? SystemColors.WindowText : PaperFieldSurface.Ink;
        Color fill = high ? (Checked ? SystemColors.Highlight : SystemColors.Window) :
            Enabled && Capture && hover ? PaperFieldSurface.Pressed :
            Checked ? (hover ? PaperFieldSurface.Focus : Color.FromArgb(239, 228, 205)) :
            hover ? PaperFieldSurface.Hover : Color.FromArgb(244, 238, 225);
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = Math.Min(7 * DeviceDpi / 96f, Math.Min(Width, Height) / 2f);
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(ClientRectangle, new SizeF(radius, radius));
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        int left = LogicalToDeviceUnits(12), size = LogicalToDeviceUnits(21);
        var logo = new Rectangle(left, (Height - size) / 2, size, size);
        if (_raster?.Width != size * 2)
        {
            _raster?.Dispose();
            _raster = _symbol.Draw(size * 2, size * 2);
        }
        if (high) g.FillRectangle(Brushes.White, logo);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(_raster, logo);
        int x = logo.Right + LogicalToDeviceUnits(8);
        int nameWidth = TextRenderer.MeasureText(g, _brand, Font, Size.Empty, Type).Width;
        using var focusFont = focus ? new Font(Font, Font.Style | FontStyle.Underline) : null;
        TextRenderer.DrawText(g, _brand, focusFont ?? Font, new Rectangle(x, 0, nameWidth, Height), Enabled ? ink : SystemColors.GrayText, Type);
        if (Source.Domestic)
        {
            int hintX = x + nameWidth + LogicalToDeviceUnits(8);
            var hintBounds = Rectangle.FromLTRB(hintX, 0, Width - LogicalToDeviceUnits(40), Height);
            TextRenderer.DrawText(g, "国内推荐", _hintFont, hintBounds, !Enabled ? SystemColors.GrayText :
                high ? ink : PaperFieldSurface.Brass, Type);
        }
        if (Checked)
        {
            var mark = new Rectangle(Width - left - LogicalToDeviceUnits(18), 0, LogicalToDeviceUnits(18), Height);
            TextRenderer.DrawText(g, "\uE73E", _checkFont, mark, !Enabled ? SystemColors.GrayText :
                high ? ink : PaperFieldSurface.Brass, Type | TextFormatFlags.HorizontalCenter);
        }
    }

    /// <summary>释放本控件持有的图标缓存和符号字体；说明字体由更新窗口统一释放。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _raster?.Dispose(); _checkFont.Dispose(); }
        base.Dispose(disposing);
    }
}
