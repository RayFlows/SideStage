/*
 * 文件名称：InlineCredentialEditor.cs
 * 文件职责：在设置侧栏内编辑 Windows 登录凭据，支持连接、保存以及确认删除本机保存项。
 * 生命周期：打开时预填当前 Windows 账户名，密码留空。已保存标志决定删除入口是否可见。
 * 输入边界：错误提示在页内显示；离开页面、成功提交或删除后清理密码输入框。
 */
using System.ComponentModel;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App;

internal sealed class InlineCredentialEditor : PaperScrollPage
{
    private readonly TextBox _account = new() { Text = Environment.UserName };
    private readonly TextBox _password = new(), _domain = new();
    private readonly SettingsSwitch _advanced;
    private readonly SettingsSwitch? _remember;
    private readonly SettingsCaption _feedback = new();
    private readonly Action<WindowsChildSessionCredentials, bool> _submit;
    private readonly Action _delete;
    private readonly SettingsActionRow _remove;
    private readonly FlowLayoutPanel _confirm = new();

    /// <summary>按入口建立表单。管理页提供保存和删除，连接页允许选择是否记住本次输入。</summary>
    internal InlineCredentialEditor(Font font, Font smallFont, bool connecting, bool hasSavedCredentials,
        Action<WindowsChildSessionCredentials, bool> submit, Action delete)
    {
        Font = font;
        Dock = DockStyle.Fill;
        _submit = submit;
        _delete = delete;
        TableLayoutPanel stack = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1 };
        DrawerMetrics.Bind(stack, () => stack.Padding = DrawerMetrics.Space(stack, 0, 0, 0, 4));
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        SetPageContent(stack);
        SettingsList fields = new();
        stack.Controls.Add(fields);
        fields.AddRow(InputRow("账户名", _account, "Windows 账户名"));
        fields.AddRow(InputRow("密码", _password, "Windows 账户密码", new PasswordVisibilityButton(_password)));
        _advanced = new SettingsSwitch("高级选项（域账户）", font);
        fields.AddRow(_advanced);
        var domainRow = InputRow("所属域", _domain, "本机账户留空");
        domainRow.Visible = _advanced.Checked;
        fields.AddRow(domainRow);
        _advanced.CheckedChanged += (_, _) => domainRow.Visible = _advanced.Checked;
        if (connecting)
        {
            _remember = new SettingsSwitch("保存到本机", font) { Checked = true };
            fields.AddRow(_remember);
        }
        stack.Controls.Add(SettingsCaption.Footnote(
            "分身用 Windows 账户密码登录，请填写账户密码而非 PIN。保存后，下次连接可自动登录。凭据仅保存在本机 Windows 凭据管理器，不会上传。", smallFont));

        var actions = new SettingsList(13, 2);
        stack.Controls.Add(actions);
        actions.AddRow(new SettingsActionRow(connecting ? "连接" : "保存凭据", font, Submit));
        _remove = new SettingsActionRow("删除已保存的凭据", font, (_, _) => SetConfirmation(true))
            { Visible = hasSavedCredentials, ForeColor = Color.FromArgb(137, 65, 52) };
        actions.AddRow(_remove);
        _feedback.AutoSize = true;
        _feedback.Dock = DockStyle.Top;
        DrawerMetrics.Margin(_feedback, PaperFieldSurface.TextInset, 8, PaperFieldSurface.TextInset, 2);
        _feedback.ForeColor = PaperFieldSurface.Ink;
        _feedback.Visible = false;
        _feedback.TextChanged += (_, _) => _feedback.Visible = _feedback.Text.Length > 0;
        stack.Controls.Add(_feedback);
        _confirm.AutoSize = true;
        DrawerMetrics.Margin(_confirm, PaperFieldSurface.TextInset, 4, 0, 0);
        _confirm.Visible = false;
        _confirm.Controls.Add(ConfirmationButton("取消", (_, _) => SetConfirmation(false)));
        _confirm.Controls.Add(ConfirmationButton("确认删除", DeleteSaved));
        stack.Controls.Add(_confirm);
        _password.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Submit(this, EventArgs.Empty);
        };
    }

    /// <summary>创建带有可访问名称的输入行，使用弹性列给长账户名保留编辑空间。</summary>
    private SettingsInputRow InputRow(string name, TextBox input, string placeholder, Button? trailingAction = null)
    {
        input.PlaceholderText = placeholder;
        input.BorderStyle = BorderStyle.None;
        return new SettingsInputRow(name, input, Font, trailingAction);
    }

    /// <summary>为删除确认提供两个键盘可操作的按钮。</summary>
    private Button ConfirmationButton(string caption, EventHandler click)
    {
        PaperCommandButton button = new() { Text = caption, Font = Font };
        DrawerMetrics.Bind(button, () => button.Size = new Size(Math.Max(button.LogicalToDeviceUnits(88),
            button.PreferredSize.Width), DrawerMetrics.RowHeight(button, 32)));
        DrawerMetrics.Margin(button, 3, 3, 3, 3);
        button.Click += click;
        return button;
    }

    /// <summary>切换侧栏内的删除确认，默认焦点落在取消上。</summary>
    private void SetConfirmation(bool show)
    {
        _confirm.Visible = show;
        _feedback.Text = show ? "删除此应用保存的本机登录凭据？" : string.Empty;
        if (show) _confirm.Controls[0].Focus();
        else _remove.Focus();
    }

    /// <summary>仅在用户第二次确认后执行删除，同时清除表单中先前的账户信息。</summary>
    private void DeleteSaved(object? sender, EventArgs e)
    {
        if (!_confirm.Visible) return;
        try
        {
            _delete();
            _account.Clear();
            _domain.Clear();
            _password.Clear();
            _advanced.Checked = false;
            _remove.Visible = _confirm.Visible = false;
            _feedback.Text = "已删除保存项。";
            if (FindForm() is Form form) form.ActiveControl = null;
        }
        catch (Win32Exception ex) { _feedback.Text = ex.Message; }
    }

    /// <summary>校验输入并按打开页面时的意图提交。回调只能在调用期间使用凭据。</summary>
    private void Submit(object? sender, EventArgs e)
    {
        if (_confirm.Visible) return;
        if (string.IsNullOrWhiteSpace(_account.Text)) { _feedback.Text = "请输入 Windows 账户名。"; _account.Focus(); return; }
        if (_password.TextLength == 0) { _feedback.Text = "请输入 Windows 账户密码。"; _password.Focus(); return; }
        try
        {
            using var credentials = CredentialInputDialog.ReadFields(_account, _advanced.Checked ? _domain.Text : "", _password);
            _submit(credentials, _remember?.Checked ?? true);
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException or InvalidDataException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _feedback.Text = ex.Message;
        }
    }

    /// <summary>侧栏收起或页面销毁时清除编辑中的密码和撤销缓冲区。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_password.IsDisposed) { _password.Clear(); _password.ClearUndo(); }
        base.Dispose(disposing);
    }
}
