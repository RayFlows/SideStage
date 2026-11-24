/*
 * 文件名称：PasswordVisibilityButton.cs
 * 文件职责：在密码输入框旁提供查看和隐藏操作，供凭据侧栏与经典窗口共用。
 * 输入边界：通过原生编辑器的遮盖字符切换显示，密码内容及选区仍由同一个输入框持有。
 */
using System.Drawing.Drawing2D;

namespace SideStage.App;

internal sealed class PasswordVisibilityButton : Button
{
    private const char SmallDot = '\u00B7';
    private readonly TextBox _field;
    private readonly ToolTip _hint = new();
    private readonly Font _iconFont = new("Segoe MDL2 Assets", 12f);

    /// <summary>绑定密码输入框，默认采用较小的居中圆点。清空密码或离开页面后恢复遮盖。</summary>
    internal PasswordVisibilityButton(TextBox field)
    {
        _field = field;
        Font = _iconFont;
        Text = "\uE890";
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = PaperFieldSurface.Focus;
        FlatAppearance.MouseDownBackColor = PaperFieldSurface.Pressed;
        ForeColor = PaperFieldSurface.Brass;
        BackColor = field.BackColor;
        Margin = Padding.Empty;
        _field.ImeMode = ImeMode.Disable;
        SetRevealed(false);
        field.BackColorChanged += MatchBackground;
        field.TextChanged += ResetWhenEmpty;
        VisibleChanged += (_, _) =>
        {
            if (!Visible && !_field.IsDisposed && _field.PasswordChar == '\0') SetRevealed(false);
        };
    }

    /// <summary>切换按钮显示的动作和编辑器遮盖字符，同时保留插入点或选中的文字范围。</summary>
    private void SetRevealed(bool reveal)
    {
        int start = _field.SelectionStart, length = _field.SelectionLength;
        _field.PasswordChar = reveal ? '\0' : SmallDot;
        _field.Select(start, length);
        AccessibleName = reveal ? "隐藏密码" : "显示密码";
        _hint.SetToolTip(this, AccessibleName);
        Invalidate();
    }

    /// <summary>鼠标点击后可继续输入，键盘激活时保留按钮焦点以便再次切换。</summary>
    protected override void OnClick(EventArgs e)
    {
        SetRevealed(_field.PasswordChar != '\0');
        if (e is MouseEventArgs) _field.Focus();
        base.OnClick(e);
    }

    /// <summary>沿用 Windows 眼睛符号，明文状态增加斜线表示下一次点击将隐藏密码。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_field.PasswordChar != '\0') return;
        float half = LogicalToDeviceUnits(8);
        float x = Width / 2f, y = Height / 2f;
        using Pen slash = new(ForeColor, 1.5f * DeviceDpi / 96f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLine(slash, x - half, y - half, x + half, y + half);
    }

    /// <summary>跟随编辑行的焦点与悬停底色。</summary>
    private void MatchBackground(object? sender, EventArgs e) => BackColor = _field.BackColor;

    /// <summary>清空输入后收起明文，下一次输入从遮盖状态开始。</summary>
    private void ResetWhenEmpty(object? sender, EventArgs e)
    {
        if (_field.TextLength == 0 && _field.PasswordChar == '\0') SetRevealed(false);
    }

    /// <summary>释放按钮持有的图标字体与提示，解除对输入框的事件订阅。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _field.BackColorChanged -= MatchBackground;
            _field.TextChanged -= ResetWhenEmpty;
            _hint.Dispose();
            _iconFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
