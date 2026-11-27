/*
 * 文件职责：统一桌面应用的模态提示、操作确认与附加选项的布局、字体和按钮。
 * 交互约定：调用方只接收选择结果。涉及状态变更的确认默认聚焦取消，Esc 或关窗均可退出。
 * 排版约定：普通消息按宽度换行；较长的诊断信息独立滚动，底部操作始终可见。
 */
namespace SideStage.App;

internal class PaperDialog : Form
{
    private readonly Font _body = TheatreTypography.Create(10);
    private readonly Font _heading = TheatreTypography.Create(16, FontStyle.Bold);
    private readonly TableLayoutPanel _grid;
    private readonly FlowLayoutPanel _actions;

    /// <summary>创建可附加复选框或列表的纸面弹窗。宽度为 96 DPI 下的正文宽度。</summary>
    internal PaperDialog(string caption, string heading, string message, int width = 384, bool error = false)
    {
        Program.ApplyIdentity(this);
        Text = caption;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        Font = _body;
        ForeColor = PaperFieldSurface.Ink;
        BackColor = PaperFieldSurface.Paper;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(22, 16, 22, 14), ColumnCount = 1, RowCount = 4
        };
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        for (int i = 0; i < _grid.RowCount; i++) _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _grid.Controls.Add(new Label { Text = heading, Font = _heading, AutoSize = true, UseMnemonic = false,
            ForeColor = error ? Color.FromArgb(137, 65, 52) : ForeColor, Margin = Padding.Empty }, 0, 0);
        var body = new Label { Text = message, Font = _body, AutoSize = true, Dock = DockStyle.Top,
            UseMnemonic = false, Margin = new Padding(0, 10, 0, 0) };
        // 用实际字体测量，长路径或多行错误不会把确认按钮挤出窗口。
        if (body.GetPreferredSize(new Size(width, 0)).Height > _body.Height * 8)
        {
            body.Dispose();
            _grid.Controls.Add(new TextBox { Text = message.ReplaceLineEndings(), Multiline = true, ReadOnly = true,
                BorderStyle = BorderStyle.None, WordWrap = true, ScrollBars = ScrollBars.Vertical,
                BackColor = BackColor, ForeColor = ForeColor, Dock = DockStyle.Top, TabStop = true,
                Height = _body.Height * 8, Margin = new Padding(0, 10, 0, 0), AccessibleName = heading }, 0, 1);
        }
        else _grid.Controls.Add(body, 0, 1);
        _actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 15, 0, 0) };
        _grid.Controls.Add(_actions, 0, 3);
        Controls.Add(_grid);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            // 系统消息框支持复制诊断。正文已有选区时，由文本框自行复制所选文字。
            if (e.KeyData != (Keys.Control | Keys.C) || ActiveControl is TextBoxBase { SelectionLength: > 0 }) return;
            try { Clipboard.SetText($"{heading}{Environment.NewLine}{message}"); }
            catch (System.Runtime.InteropServices.ExternalException) { /* 剪贴板忙时保留弹窗供用户重试。 */ }
            e.SuppressKeyPress = true;
        };
    }

    /// <summary>在正文与按钮之间放置当前操作专用的选项，由窗体负责释放。</summary>
    internal void AddOptions(Control options) => _grid.Controls.Add(options, 0, 2);

    /// <summary>从右向左添加按钮，并关联其模态返回值。默认和取消操作由调用方明确指定。</summary>
    internal PaperCommandButton AddAction(string caption, DialogResult result, bool accent = false)
    {
        var button = PaperCommandButton.DialogAction(caption, accent);
        button.DialogResult = result;
        _actions.Controls.Add(button);
        return button;
    }

    /// <summary>为状态变更增加确认与取消按钮。初始回车、Esc 和关闭键均保留原状态。</summary>
    internal void AddConfirmation(string action, string cancel = "取消")
    {
        AddAction(action, DialogResult.OK, accent: true);
        var leave = AddAction(cancel, DialogResult.Cancel);
        AcceptButton = CancelButton = leave;
        ActiveControl = leave;
    }

    /// <summary>显示具体操作和后果，仅显式确认时向调用方返回成功。</summary>
    internal static bool Confirm(IWin32Window? owner, string title, string message, string action)
    {
        using var dialog = new PaperDialog(Program.DisplayName, title, message);
        dialog.AddConfirmation(action);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    /// <summary>显示信息或错误。更新交接失败时主窗口可能已经退出，此时在屏幕中央独立显示。</summary>
    internal static void Tell(IWin32Window? owner, string title, string message, bool error = false)
    {
        using var dialog = new PaperDialog(Program.DisplayName, title, message, error: error);
        var done = dialog.AddAction("确定", DialogResult.OK, accent: true);
        dialog.AcceptButton = dialog.CancelButton = done;
        dialog.ActiveControl = done;
        if (owner == null) dialog.StartPosition = FormStartPosition.CenterScreen;
        dialog.ShowDialog(owner);
    }

    /// <summary>子控件释放后归还本窗创建的字体。</summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _body.Dispose(); _heading.Dispose(); }
    }
}
