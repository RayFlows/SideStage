/*
 * 文件名称：PaperSettingsFields.cs
 * 文件职责：为侧栏提供统一的文字边距、无描边的纸色分组，以及占满整行的原生下拉选项。
 * 输入边界：选择、键盘导航与辅助功能沿用 ComboBox。日志文字由 RichTextBox 负责。
 * 缩放约定：圆角、箭头和文字留白使用逻辑像素，随所在显示器的 DPI 缩放。
 */
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SideStage.App;

internal static class PaperFieldSurface
{
    internal static readonly Color Paper = Color.FromArgb(249, 244, 232);
    internal static readonly Color Ink = Color.FromArgb(78, 60, 45);
    internal static readonly Color Brass = Color.FromArgb(124, 95, 59);
    internal static readonly Color Hover = Color.FromArgb(232, 218, 195);
    internal static readonly Color Focus = Color.FromArgb(227, 212, 186);
    internal static readonly Color Pressed = Color.FromArgb(218, 199, 166);
    internal const int TextInset = 14;
    internal const TextFormatFlags RowText = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

    /// <summary>计算整行的文字内沿。标题、字段和动作使用同一条逻辑像素基线。</summary>
    internal static Rectangle Content(Control row) =>
        Rectangle.Inflate(row.ClientRectangle, -row.LogicalToDeviceUnits(TextInset), 0);

    /// <summary>按整行命中判断交互底色。键盘焦点使用较深的填色，禁用行保留普通纸色。</summary>
    internal static Color RowColor(Control row, bool focus, bool pressed = false)
    {
        if (!row.Enabled) return Paper;
        if (pressed) return Pressed;
        if (focus) return Focus;
        return row.ClientRectangle.Contains(row.PointToClient(Control.MousePosition)) ? Hover : Paper;
    }

    /// <summary>在控件的客户区内绘制纸面。分组内的行共用父组的圆角坐标，悬停时只裁去最外侧的四角。</summary>
    internal static void Paint(Graphics graphics, Control field, Color fill)
    {
        Control group = field.Parent is SettingsList list ? list : field;
        Rectangle bounds = group.ClientRectangle;
        if (group != field) bounds.Offset(-field.Left, -field.Top);
        graphics.Clear(group.Parent?.BackColor ?? field.BackColor);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        float radius = Math.Min(8 * field.DeviceDpi / 96f, Math.Min(bounds.Width, bounds.Height) / 2f);
        using GraphicsPath outline = new();
        outline.AddRoundedRectangle(bounds, new SizeF(radius, radius));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using SolidBrush background = new(fill);
        graphics.FillPath(background, outline);
        if (group != field && group is SettingsList rows) rows.PaintDividerAbove(graphics, field, fill);
    }
}

/// <summary>侧栏标题和说明。使用与行内标签相同的文字绘制规则，避免系统 Label 额外补入字距。</summary>
internal sealed class SettingsCaption : Label
{
    /// <summary>生成设置分组下的小字说明，在同一条文字基线上按侧栏宽度自动换行。</summary>
    internal static SettingsCaption Footnote(string text, Font smallFont) => new()
    {
        Text = text, Font = smallFont, ForeColor = Color.FromArgb(101, 85, 67),
        AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(PaperFieldSurface.TextInset, 2, PaperFieldSurface.TextInset, 0),
    };

    /// <summary>从控件的左缘绘字，外部布局统一承担缩进。保留 Label 的测量、换行与辅助功能。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>纸色下拉框。收拢时绘制字段与箭头，展开时在系统选项列表上绘制同色纸底和选中条。</summary>
internal sealed class PaperComboBox : ComboBox
{
    /// <summary>建立只读选择器，保留系统下拉列表和可访问名称。</summary>
    internal PaperComboBox(string label)
    {
        AccessibleName = label;
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = PaperFieldSurface.Paper;
        ForeColor = PaperFieldSurface.Ink;
        Cursor = Cursors.Hand;
        MouseEnter += (_, _) => Invalidate();
        MouseLeave += (_, _) => Invalidate();
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
        SelectedIndexChanged += (_, _) => Invalidate();
        DropDownClosed += (_, _) => Invalidate();
    }

    /// <summary>按当前字体预留选项上下空间，使不同字号和 DPI 下的中文保持完整。</summary>
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ItemHeight = Math.Max(Font.Height + LogicalToDeviceUnits(6), LogicalToDeviceUnits(36) - 6);
    }

    /// <summary>整个选择行都是同一个原生控件。左边绘制字段名，右边绘制当前值和展开标记。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        bool open = DroppedDown;
        Color ink = Enabled ? ForeColor : SystemColors.GrayText;
        PaperFieldSurface.Paint(e.Graphics, this, PaperFieldSurface.RowColor(this, Focused && ShowFocusCues, open));
        Rectangle content = PaperFieldSurface.Content(this);
        // 名称和当前值各占半行，不让带省略号的测量结果反过来压缩名称本身。
        Rectangle label = content;
        label.Width /= 2;
        TextRenderer.DrawText(e.Graphics, AccessibleName, Font, label, ink, PaperFieldSurface.RowText);
        int gap = LogicalToDeviceUnits(8);
        Rectangle value = Rectangle.FromLTRB(label.Right + gap, 0, content.Right - 2 * gap, Height);
        TextRenderer.DrawText(e.Graphics, GetItemText(SelectedItem), Font, value, Enabled ? PaperFieldSurface.Brass : ink,
            PaperFieldSurface.RowText | TextFormatFlags.Right);

        float unit = DeviceDpi / 96f;
        float x = content.Right - 4 * unit, y = Height / 2f;
        float dy = open ? -2 * unit : 2 * unit;
        using Pen pen = new(Enabled ? PaperFieldSurface.Brass : ink, 1.5f * unit) { LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, new PointF[] { new(x - 4 * unit, y - dy), new(x, y + dy), new(x + 4 * unit, y - dy) });
    }

    /// <summary>为系统弹出的列表项加上暖金选中底色，使用与关闭状态一致的字距和文字起点。</summary>
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using SolidBrush fill = new(selected ? Color.FromArgb(231, 214, 184) : BackColor);
        e.Graphics.FillRectangle(fill, e.Bounds);
        Rectangle text = e.Bounds;
        text.Inflate(-LogicalToDeviceUnits(9), 0);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, text, ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        base.OnDrawItem(e);
    }

    /// <summary>展开时同步箭头方向，在支持此设置的桌面上为原生浮层应用小圆角。</summary>
    protected override void OnDropDown(EventArgs e)
    {
        base.OnDropDown(e);
        Invalidate();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        ComboInfo info = new() { Size = Marshal.SizeOf<ComboInfo>() };
        if (GetComboBoxInfo(Handle, ref info) && info.List != 0)
        {
            const int cornerPreference = 33, roundSmall = 3;
            DwmSetWindowAttribute(info.List, cornerPreference, roundSmall, sizeof(int));
        }
    }

    /// <summary>对应 Windows 的 COMBOBOXINFO，列出字段、按钮和独立列表窗口。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ComboInfo
    {
        internal int Size;
        internal Rectangle ItemBounds, ButtonBounds;
        internal uint ButtonState;
        internal nint Combo, Edit, List;
    }

    /// <summary>取得当前控件自己的弹出列表句柄。</summary>
    [DllImport("user32.dll")]
    private static extern bool GetComboBoxInfo(nint combo, ref ComboInfo info);

    /// <summary>把浮层圆角交给桌面合成器处理，列表仍由系统负责命中和滚动。</summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);
}
