/*
 * 文件名称：EditionIndicator.cs
 * 文件职责：在操作系统一行显示版本标记。家庭版使用琥珀色三角警示和与日志相同的圆角气泡。
 * 交互边界：提示不抢占连接操作；鼠标可悬停在行上，键盘用户也可通过 Tab、回车和 Esc 阅读说明。
 */
namespace SideStage.App;

internal sealed class EditionIndicator : Label
{
    private const string HomeNote = $"家庭版不能接收来自其他电脑的远程桌面连接。{Program.DisplayName} 连接的是本机子会话，仍可尝试，能否登录以实际连接为准。";
    private readonly Font _textFont, _symbolFont;
    private readonly PaperHintBubble _bubble;
    private readonly System.Windows.Forms.Timer _leaveTimer = new() { Interval = 280 };
    private readonly List<Control> _targets = [];

    /// <summary>建立固定大小的状态图标和可换行的说明浮层。警示使用系统自带的线形图标，不使用彩色表情。</summary>
    internal EditionIndicator(Font textFont, Font symbolFont)
    {
        _textFont = textFont;
        _symbolFont = symbolFont;
        Font = textFont;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        TextAlign = ContentAlignment.MiddleLeft;
        SetStyle(ControlStyles.Selectable, true);
        _bubble = new PaperHintBubble(this, tipAtLeft: true);
        Label body = new() { Text = HomeNote, Font = textFont, AutoSize = true, MaximumSize = new Size(270, 0),
            Padding = new Padding(12, 9, 12, 10), Margin = Padding.Empty, BackColor = PaperHintBubble.Surface,
            ForeColor = Color.FromArgb(249, 243, 229) };
        _bubble.Items.Add(new ToolStripControlHost(body) { Margin = Padding.Empty, Padding = Padding.Empty });
        _bubble.Closed += (_, _) => _leaveTimer.Stop();
        _leaveTimer.Tick += (_, _) =>
        {
            if (!_bubble.Bounds.Contains(MousePosition) && !_targets.Any(c => c.ClientRectangle.Contains(c.PointToClient(MousePosition))))
                _bubble.Close();
        };
        BindHover(this);
        Click += (_, _) => ShowNote(false);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Space or Keys.Escape)) return;
            e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.Escape) _bubble.Close(); else ShowNote(false);
        };
        LostFocus += (_, _) => { if (!_leaveTimer.Enabled) _bubble.Close(); };
        VisibleChanged += (_, _) => { if (!Visible) _bubble.Close(); };
        Apply(EditionMark.Unknown);
    }

    /// <summary>让图标、字段名和版本文字都能唤出同一条家庭版说明。</summary>
    internal void BindHover(Control target)
    {
        _targets.Add(target);
        target.MouseHover += (_, _) => ShowNote(true);
    }

    /// <summary>只在家庭版启用交互。没有充分证据时显示中性色空心圆，不把未知系统当作故障。</summary>
    internal void Apply(EditionMark mark)
    {
        _bubble.Close();
        TabStop = mark == EditionMark.Home;
        Font = TabStop ? _symbolFont : _textFont;
        (Text, ForeColor, AccessibleName) = mark switch
        {
            EditionMark.ListedHost => ("✓", Color.FromArgb(100, 140, 108), "远程桌面主机版本"),
            EditionMark.Home => ("\uE7BA", Color.FromArgb(156, 109, 26), "家庭版兼容性提示"),
            _ => ("○", Color.FromArgb(168, 144, 108), "系统兼容性待确认"),
        };
        AccessibleDescription = TabStop ? HomeNote : null;
        AccessibleRole = TabStop ? AccessibleRole.PushButton : AccessibleRole.Graphic;
        foreach (Control target in _targets) target.Cursor = TabStop ? Cursors.Help : Cursors.Default;
    }

    /// <summary>气泡朝向行内文字展开。鼠标移出后自动收起，键盘激活时可以用 Esc 关闭。</summary>
    private void ShowNote(bool hover)
    {
        if (!TabStop || !Visible) return;
        if (!_bubble.Visible)
        {
            _bubble.Show(this, new Point(0, Height), ToolStripDropDownDirection.BelowRight);
            _bubble.UpdateEdge();
        }
        if (hover) _leaveTimer.Start(); else _leaveTimer.Stop();
    }

    /// <summary>窗口销毁时释放气泡的原生边缘与计时器；字体由所属窗体统一持有。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _bubble.Close(); _bubble.Dispose(); _leaveTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
