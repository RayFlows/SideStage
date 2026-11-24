/*
 * 文件名称：PaperCommandButton.cs
 * 文件职责：为首页、设置侧栏、凭据窗口和标题栏提供同一圆角轮廓及交互反馈。
 * 输入边界：由 WinForms Button 处理回车、空格、助记键和辅助技术调用，此处只绘制外观。
 * 绘制约定：按钮面和底色单独绘制，使抗锯齿圆角可以透出首页纸纹或标题栏颜色。
 */
using System.Drawing.Drawing2D;

namespace SideStage.App;

internal class PaperCommandButton : Button
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Color FaceColor { get; set; } = PaperFieldSurface.Paper;
    /// <summary>圆形导航键使用短边作为直径，交互反馈始终填充同一圆面。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Circular { get; init; }
    private bool _hovered;
    private bool _spaceDown;
    private bool _default;

    /// <summary>启用双缓冲和透明底，跟踪指针及空格键状态，让鼠标和键盘操作得到对应的反馈。</summary>
    internal PaperCommandButton()
    {
        FlatStyle = FlatStyle.Flat;
        BackColor = Color.Transparent;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
        MouseCaptureChanged += (_, _) => Invalidate();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Space) { _spaceDown = true; Invalidate(); } };
        KeyUp += (_, e) => { if (e.KeyCode == Keys.Space) { _spaceDown = false; Invalidate(); } };
        LostFocus += (_, _) => { _spaceDown = false; Invalidate(); };
    }

    /// <summary>保留窗体的默认按钮语义，让默认操作获得轻微强调的底色。</summary>
    public override void NotifyDefault(bool value)
    {
        base.NotifyDefault(value);
        _default = value;
        Invalidate();
    }

    /// <summary>绘制固定位置的圆角按钮与标签，用纯色填充区分普通、悬停、按下和禁用状态。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        // ButtonBase 可跳过背景擦除。先填满控件矩形，避免圆弧以外残留默认按钮的浅色像素。
        Color backing = BackColor;
        for (Control? parent = Parent; backing.A == 0 && parent != null; parent = parent.Parent)
            backing = parent.BackColor;
        e.Graphics.Clear(backing);
        Color fill = FaceColor;
        Color text = Enabled ? ForeColor : SystemColors.GrayText;
        bool pressed = Enabled && (_spaceDown || (_hovered && Capture && (MouseButtons & MouseButtons.Left) != 0));
        if (!Enabled) fill = ControlPaint.Light(FaceColor, .3f);
        else if (pressed) fill = Mix(FaceColor, PaperFieldSurface.Brass, .32f);
        else if (Focused && ShowFocusCues) fill = Mix(FaceColor, ContrastingTint(), .26f);
        else if (_hovered)
            fill = FlatAppearance.MouseOverBackColor.IsEmpty ? Mix(FaceColor, ContrastingTint(), .18f) : FlatAppearance.MouseOverBackColor;
        else if (_default) fill = Mix(FaceColor, ContrastingTint(), .08f);

        float scale = DeviceDpi / 96f;
        var face = new RectangleF(0, 0, Width, Height);
        if (face.Width < 2 || face.Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using SolidBrush surface = new(fill);
        if (Circular)
        {
            float diameter = Math.Min(face.Width, face.Height);
            e.Graphics.FillEllipse(surface, (face.Width - diameter) / 2, (face.Height - diameter) / 2, diameter, diameter);
        }
        else
        {
            using GraphicsPath outline = RoundedOutline(face, 8 * scale);
            e.Graphics.FillPath(surface, outline);
        }
        Rectangle content = ClientRectangle;
        content = Rectangle.FromLTRB(content.Left + Padding.Left, content.Top, content.Right - Padding.Right, content.Bottom);
        TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        flags |= TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
        if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
        else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(e.Graphics, Text, Font, content, text, flags);
    }

    /// <summary>按按钮明度选择交互色，深色主命令和浅色导航都能清晰地显示整面反馈。</summary>
    private Color ContrastingTint() => FaceColor.GetBrightness() < .5f ? Color.White : PaperFieldSurface.Brass;

    /// <summary>用四段四分之一圆确定实际圆角半径，窄按钮的半径限制为短边的一半。</summary>
    private static GraphicsPath RoundedOutline(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>将按钮底色略微染成暖金色，提供按压反馈。</summary>
    private static Color Mix(Color source, Color tint, float amount) => Color.FromArgb(
        (int)Math.Round(source.R + (tint.R - source.R) * amount),
        (int)Math.Round(source.G + (tint.G - source.G) * amount),
        (int)Math.Round(source.B + (tint.B - source.B) * amount));
}
