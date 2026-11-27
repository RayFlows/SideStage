/*
 * 文件职责：在启动项抽屉中绘制应用图标、名称、来源和独立开关。
 * 交互约定：可编辑行沿用原生 CheckBox 的鼠标与键盘操作。只读行显示原因，
 * 不放置会让用户误以为能切换的开关。图标由本行持有并在销毁时释放。
 */
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SideStage.StartupGate;

namespace SideStage.App;

internal sealed class StartupAppRow : CheckBox
{
    private readonly Font _caption;
    private readonly Bitmap _icon;
    private readonly string _description;
    private string _wrappedTitle = "";
    private string _wrappedDetail = "";
    private int _measuredWidth, _measuredDpi, _measuredHeight;
    private Font? _measuredFont;
    private readonly bool _editable;
    private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    /// <summary>用实际登记建立整行开关。名称与说明独占文字列，字号放大后增加行高。</summary>
    internal StartupAppRow(StartupEntry item, Font title, Font caption, ToolTip hints, Action<StartupAppRow> clicked)
    {
        Text = item.DisplayName ?? item.Name;
        Font = title;
        _caption = caption;
        _editable = item.Fingerprint != null && (item.ReadOnlyReason == null || item.Blocked);
        _description = Describe(item);
        const string appBehavior = "应用自身是否允许重复启动未知。";
        hints.SetToolTip(this, $"{item.Name}\n{item.Detail}\n{_description}\n{appBehavior}");
        _icon = LoadIcon(item.IconPath);
        AccessibleName = Text;
        AccessibleDescription = $"{_description}\n{appBehavior}";
        AccessibleRole = _editable ? AccessibleRole.CheckButton : AccessibleRole.StaticText;
        AutoCheck = false;
        AutoSize = true;
        TabStop = _editable;
        Cursor = _editable ? Cursors.Hand : Cursors.Default;
        Checked = !item.Blocked;
        DoubleBuffered = true;
        ResizeRedraw = true;
        if (_editable) Click += (_, _) => clicked(this);
    }

    /// <summary>逐行显示系统登记状态、操作限制和同一任务的重复触发策略。分身开关独立于 Windows 状态。</summary>
    private static string Describe(StartupEntry item)
    {
        string source = item.Source switch
        {
            "CurrentUser" => "个人登录项", "LocalMachine" => "全机登录项", "Startup" => "个人启动文件夹",
            "CommonStartup" => "公共启动文件夹", "Task" => "登录计划任务", "Package" => "应用包启动项", _ => item.Detail
        };
        string state = item.WindowsEnabled switch { true => "Windows 已启用", false => "Windows 已禁用", null => "Windows 启用状态未知" };
        var lines = new List<string> { $"{source} · {state}" };
        if (item.ReadOnlyReason != null)
        {
            bool canRestore = item.Blocked && item.Fingerprint != null;
            lines.Add($"{(canRestore ? "仅可恢复" : "只读")} · {item.ReadOnlyReason}");
        }
        if (item.Source == "Task")
        {
            lines.Add(item.RepeatPolicy switch
            {
                StartupRepeatPolicy.IgnoreNew => "任务运行中再次触发时，跳过本次启动。",
                StartupRepeatPolicy.Parallel => "任务运行中再次触发时，同时运行新实例。",
                StartupRepeatPolicy.Queue => "任务运行中再次触发时，排队等候执行。",
                StartupRepeatPolicy.StopExisting => "任务运行中再次触发时，先停止旧实例。",
                _ => "任务重复触发规则未知。"
            });
        }
        return string.Join('\n', lines);
    }

    /// <summary>文字区域两侧分别预留图标和滑块位置，只读行把右侧空间还给文字。</summary>
    private Rectangle TextArea()
    {
        Rectangle area = PaperFieldSurface.Content(this);
        int indent = LogicalToDeviceUnits(36);
        return new(area.Left + indent, 0, Math.Max(1, area.Width - indent - (_editable ? LogicalToDeviceUnits(44) : 0)), Height);
    }

    /// <summary>由表格在分配行空间前按列宽询问首选高度，避免用临时窄列的高度留出多余空白。</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (_caption == null) return base.GetPreferredSize(proposedSize);
        int width = proposedSize.Width is > 1 and < int.MaxValue ? proposedSize.Width : Math.Max(Width, LogicalToDeviceUnits(120));
        int inset = 2 * LogicalToDeviceUnits(PaperFieldSurface.TextInset) + LogicalToDeviceUnits(36)
            + (_editable ? LogicalToDeviceUnits(44) : 0);
        return new Size(width, MeasureTextHeight(Math.Max(1, width - inset)));
    }

    /// <summary>布局与绘制共享当前文字列的测量，不在尺寸回调中改写控件高度。</summary>
    private int MeasureTextHeight(int width)
    {
        if (width == _measuredWidth && _measuredDpi == DeviceDpi && _measuredFont == Font) return _measuredHeight;
        var limit = new Size(width, int.MaxValue);
        _wrappedTitle = WrapIdentifiers(Text, Font, limit.Width);
        _wrappedDetail = string.Join('\n', _description.Split('\n').Select(line => WrapIdentifiers(line, _caption, limit.Width)));
        int height = TextRenderer.MeasureText(_wrappedTitle, Font, limit, Wrap).Height
            + TextRenderer.MeasureText(_wrappedDetail, _caption, limit, Wrap).Height + LogicalToDeviceUnits(15);
        _measuredWidth = width;
        _measuredDpi = DeviceDpi;
        _measuredFont = Font;
        return _measuredHeight = Math.Max(LogicalToDeviceUnits(50), height);
    }

    /// <summary>原生换行不会拆开超长产品名和账户编号。仅对超过整列宽度的单词按文字元素拆行。</summary>
    private static string WrapIdentifiers(string text, Font font, int width)
    {
        const TextFormatFlags single = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var output = new List<string>();
        foreach (var word in text.Split(' '))
        {
            if (TextRenderer.MeasureText(word, font, Size.Empty, single).Width <= width) { output.Add(word); continue; }
            var parts = new List<string>();
            var line = new StringBuilder();
            var letters = StringInfo.GetTextElementEnumerator(word);
            while (letters.MoveNext())
            {
                string next = letters.GetTextElement();
                if (line.Length != 0 && TextRenderer.MeasureText(line.ToString() + next, font, Size.Empty, single).Width > width)
                { parts.Add(line.ToString()); line.Clear(); }
                line.Append(next);
            }
            parts.Add(line.ToString());
            output.Add(string.Join("\n", parts));
        }
        return string.Join(" ", output);
    }

    /// <summary>绘制与侧栏一致的纸色行，左图标与右侧开关相对整行垂直居中。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var surface = e.Graphics;
        PaperFieldSurface.Paint(surface, this, _editable ? PaperFieldSurface.RowColor(this, Focused && ShowFocusCues) : PaperFieldSurface.Paper);
        Rectangle body = PaperFieldSurface.Content(this), area = TextArea();
        MeasureTextHeight(area.Width);
        int icon = LogicalToDeviceUnits(26);
        surface.InterpolationMode = InterpolationMode.HighQualityBicubic;
        surface.DrawImage(_icon, new Rectangle(body.Left, (Height - icon) / 2, icon, icon));
        int titleHeight = TextRenderer.MeasureText(_wrappedTitle, Font, new Size(area.Width, int.MaxValue), Wrap).Height;
        int top = LogicalToDeviceUnits(6);
        TextRenderer.DrawText(surface, _wrappedTitle, Font, new Rectangle(area.X, top, area.Width, titleHeight), PaperFieldSurface.Ink, Wrap);
        top += titleHeight + LogicalToDeviceUnits(2);
        TextRenderer.DrawText(surface, _wrappedDetail, _caption, new Rectangle(area.X, top, area.Width, Math.Max(1, Height - top)), PaperFieldSurface.Brass, Wrap);
        if (_editable) SettingsSwitch.DrawTrack(surface, this, body.Right, Checked);
    }

    /// <summary>优先读本地应用图标。包图像载入后立即关闭文件，缺失资源使用系统应用图标。</summary>
    private static Bitmap LoadIcon(string? path)
    {
        try
        {
            if (path != null && Dispatcher.LocalPath(path) && File.Exists(path))
            {
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    using var original = Image.FromFile(path);
                    return new Bitmap(original, 48, 48);
                }
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    using var original = Icon.ExtractIcon(path, 0, 48);
                    if (original != null) return original.ToBitmap();
                }
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or ExternalException or OutOfMemoryException) { }
        return SystemIcons.Application.ToBitmap();
    }

    /// <summary>分组折叠、刷新或离开页面时释放当前行的图像。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon.Dispose();
        base.Dispose(disposing);
    }
}
