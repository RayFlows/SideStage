/*
 * 文件名称：CredentialInputDialog.cs
 * 文件职责：
 *   提供经典窗口的登录凭据设置，供用户保存、替换或删除本机凭据。
 *
 * 安全边界：
 *   - 不把密码写入事件日志、配置文件、命令行参数或窗口标题。
 *   - 密码默认以小圆点遮盖，可由用户主动查看；确认后立即清空控件内容。
 *   - 长期保存只调用 WindowsChildSessionCredentialStore，不实现网络访问。
 *   - RDP 连接仍由 Windows 登录组件处理；此对话框不模拟键盘输入。
 */

using System.Drawing;
using System.Security;
using System.Windows.Forms;
using SideStage.Windows.Platform.Windows;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App;

/// <summary>
/// 管理当前用户保存在 Windows 凭据管理器中的子会话登录凭据。
/// </summary>
internal sealed class CredentialInputDialog : Form
{
    private static readonly Color WindowColor = PaperFieldSurface.Paper;
    private static readonly Color SurfaceColor = PaperFieldSurface.Hover;
    private static readonly Color TextColor = PaperFieldSurface.Ink;
    private static readonly Color MutedTextColor = Color.FromArgb(103, 82, 65);
    private static readonly Color AccentColor = PaperFieldSurface.Brass;
    private readonly Font _dialogFont = TheatreTypography.Create(10);
    private readonly WindowsChildSessionCredentialStore _credentialStore;
    private readonly TextBox _userNameInput = new();
    private readonly TextBox _domainInput = new();
    private readonly TextBox _passwordInput = new();
    private readonly TableLayoutPanel _rootLayout = new();
    private readonly PaperCommandButton _advancedOptionsButton = new();
    private readonly PaperCommandButton _deleteSavedButton = new();
    private readonly PaperCommandButton _confirmButton = new();
    private Label? _domainLabel;
    private Label? _domainHelpLabel;
    private bool _advancedOptionsExpanded;

    /// <summary>
    /// 建立子会话登录凭据输入窗口。
    /// </summary>
    /// <param name="credentialStore">当前 Windows 用户的凭据管理器访问器。</param>
    public CredentialInputDialog(WindowsChildSessionCredentialStore credentialStore)
    {
        _credentialStore = credentialStore;

        Program.ApplyIdentity(this);
        Text = "Windows 登录凭据";
        BackColor = WindowColor;
        ForeColor = TextColor;
        Font = _dialogFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        BuildLayout();
        LoadSavedAccount();
        _deleteSavedButton.Enabled = _credentialStore.HasSavedCredentials();
    }

    /// <summary>
    /// 清除未保存的密码草稿并释放对话框字体。
    /// </summary>
    /// <param name="disposing">是否执行托管资源释放。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_passwordInput.IsDisposed) { _passwordInput.Clear(); _passwordInput.ClearUndo(); }

        base.Dispose(disposing);
        if (disposing) _dialogFont.Dispose();
    }

    /// <summary>
    /// 创建凭据输入区、保存说明和操作按钮。
    /// </summary>
    private void BuildLayout()
    {
        _rootLayout.SuspendLayout();
        _rootLayout.Dock = DockStyle.Fill;
        _rootLayout.Padding = new Padding(20);
        _rootLayout.ColumnCount = 2;
        _rootLayout.RowCount = 8;
        _rootLayout.BackColor = WindowColor;
        _rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        _rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        Label note = new()
        {
            Text = "请输入 Windows 账户密码，不是 PIN。密码只交给本机 Windows RDP 登录组件。",
            ForeColor = MutedTextColor,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _rootLayout.Controls.Add(note, 0, 0);
        _rootLayout.SetColumnSpan(note, 2);

        ConfigureLabelAndInput(_rootLayout, "账户名", _userNameInput, 1);
        ConfigureLabelAndInput(_rootLayout, "账户密码", _passwordInput, 2, new PasswordVisibilityButton(_passwordInput));

        _advancedOptionsButton.Text = "高级选项（域账号才需要） +";
        _advancedOptionsButton.Dock = DockStyle.Fill;
        _advancedOptionsButton.FlatStyle = FlatStyle.Flat;
        _advancedOptionsButton.FlatAppearance.BorderSize = 0;
        _advancedOptionsButton.FaceColor = SurfaceColor;
        _advancedOptionsButton.ForeColor = AccentColor;
        _advancedOptionsButton.TextAlign = ContentAlignment.MiddleLeft;
        _advancedOptionsButton.Margin = Padding.Empty;
        _advancedOptionsButton.Click += ToggleAdvancedOptions;
        _rootLayout.Controls.Add(_advancedOptionsButton, 0, 3);
        _rootLayout.SetColumnSpan(_advancedOptionsButton, 2);

        _domainLabel = new Label
        {
            Text = "账户所属域",
            Dock = DockStyle.Fill,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _domainInput.Dock = DockStyle.Fill;
        _domainInput.Margin = new Padding(0, 4, 0, 4);
        _domainInput.BackColor = SurfaceColor;
        _domainInput.ForeColor = TextColor;
        _domainInput.BorderStyle = BorderStyle.FixedSingle;
        _rootLayout.Controls.Add(_domainLabel, 0, 4);
        _rootLayout.Controls.Add(_domainInput, 1, 4);

        _domainHelpLabel = new Label
        {
            Text = "本机账户留空。只有使用公司或学校的域账户时才填写，例如 ACME\\user 中的 ACME。",
            Dock = DockStyle.Fill,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _rootLayout.Controls.Add(_domainHelpLabel, 0, 5);
        _rootLayout.SetColumnSpan(_domainHelpLabel, 2);

        Label securityNote = new()
        {
            Text = "保存后由 Windows 按当前用户保护；连接时本机应用会读取凭据并交给 RDP。不会上传。",
            ForeColor = MutedTextColor,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _rootLayout.Controls.Add(securityNote, 0, 6);
        _rootLayout.SetColumnSpan(securityNote, 2);

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = WindowColor,
        };
        ConfigureActionButton(_confirmButton, "保存凭据", OnConfirm, AccentColor);
        ConfigureActionButton(_deleteSavedButton, "删除保存项", OnDeleteSaved, SurfaceColor);

        actions.Controls.Add(_confirmButton);
        actions.Controls.Add(_deleteSavedButton);
        _rootLayout.Controls.Add(actions, 0, 7);
        _rootLayout.SetColumnSpan(actions, 2);
        _rootLayout.ResumeLayout(performLayout: true);
        Controls.Add(_rootLayout);
        AcceptButton = _confirmButton;
        CancelButton = new Button { DialogResult = DialogResult.Cancel };
        SetAdvancedOptionsExpanded(expanded: false);
    }

    /// <summary>
    /// 切换高级选项显示状态；域字段默认收起，存在已保存域值时会自动展开。
    /// </summary>
    /// <param name="sender">触发切换的高级选项按钮。</param>
    /// <param name="eventArgs">按钮事件参数。</param>
    private void ToggleAdvancedOptions(object? sender, EventArgs eventArgs)
    {
        SetAdvancedOptionsExpanded(!_advancedOptionsExpanded);
    }

    /// <summary>
    /// 展开或收起可选域设置，并同步调整对话框高度。
    /// </summary>
    /// <param name="expanded">是否显示高级选项。</param>
    private void SetAdvancedOptionsExpanded(bool expanded)
    {
        _advancedOptionsExpanded = expanded;
        _domainLabel!.Visible = expanded;
        _domainInput.Visible = expanded;
        _domainHelpLabel!.Visible = expanded;
        _rootLayout.RowStyles[4].Height = expanded ? 38 : 0;
        _rootLayout.RowStyles[5].Height = expanded ? 38 : 0;
        _advancedOptionsButton.Text = expanded
            ? "高级选项（域账号才需要） -"
            : "高级选项（域账号才需要） +";
        ClientSize = new Size(500, _rootLayout.Padding.Vertical + _rootLayout.RowStyles.Cast<RowStyle>().Sum(row => (int)row.Height));
    }

    /// <summary>
    /// 在表格指定行放置标签与单行输入框。
    /// </summary>
    /// <param name="layout">目标表格布局。</param>
    /// <param name="labelText">字段名称。</param>
    /// <param name="input">字段输入框。</param>
    /// <param name="row">表格行索引。</param>
    /// <param name="trailingAction">可选的行尾按钮，用于在密码输入框旁切换可见性。</param>
    private static void ConfigureLabelAndInput(
        TableLayoutPanel layout,
        string labelText,
        TextBox input,
        int row,
        Button? trailingAction = null)
    {
        Label label = new()
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 4, 0, 4);
        input.BackColor = SurfaceColor;
        input.ForeColor = TextColor;
        input.BorderStyle = BorderStyle.FixedSingle;
        layout.Controls.Add(label, 0, row);
        if (trailingAction is null)
        {
            layout.Controls.Add(input, 1, row);
            return;
        }
        var field = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var actionColumn = new ColumnStyle(SizeType.Absolute);
        field.ColumnStyles.Add(actionColumn);
        DrawerMetrics.Bind(field, () => actionColumn.Width = field.LogicalToDeviceUnits(30));
        field.Controls.Add(input, 0, 0);
        trailingAction.Dock = DockStyle.Fill;
        trailingAction.Margin = new Padding(0, 4, 0, 4);
        field.Controls.Add(trailingAction, 1, 0);
        layout.Controls.Add(field, 1, row);
    }

    /// <summary>
    /// 设置凭据操作按钮的基本外观和点击行为。
    /// </summary>
    /// <param name="button">目标按钮。</param>
    /// <param name="text">按钮文字。</param>
    /// <param name="onClick">按钮事件处理器。</param>
    /// <param name="background">按钮背景颜色。</param>
    private static void ConfigureActionButton(
        PaperCommandButton button,
        string text,
        EventHandler onClick,
        Color background)
    {
        button.Text = text;
        button.AutoSize = true;
        button.MinimumSize = new Size(88, 34);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FaceColor = background;
        button.ForeColor = background == AccentColor ? Color.White : TextColor;
        button.Margin = new Padding(4, 0, 0, 0);
        button.Click += onClick;
    }

    /// <summary>
    /// 读取已保存条目的非密码部分用于显示；不把密码填回输入框。
    /// </summary>
    private void LoadSavedAccount()
    {
        using WindowsChildSessionCredentials? credentials = _credentialStore.Read();
        if (credentials is null)
        {
            return;
        }

        _userNameInput.Text = credentials.UserName;
        _domainInput.Text = credentials.Domain;
        // 已保存的域值不能在收起状态下悄悄参与登录，因此存在域时自动展开供用户确认。
        if (!string.IsNullOrWhiteSpace(credentials.Domain))
        {
            SetAdvancedOptionsExpanded(expanded: true);
        }
    }

    /// <summary>
    /// 收集并保存输入，返回成功时释放本次使用的凭据对象。
    /// </summary>
    /// <param name="sender">触发操作的按钮。</param>
    /// <param name="eventArgs">按钮事件参数。</param>
    private void OnConfirm(object? sender, EventArgs eventArgs)
    {
        try
        {
            using var credentials = ReadFields(_userNameInput, _advancedOptionsExpanded ? _domainInput.Text : "", _passwordInput);
            _credentialStore.Save(credentials);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception) when (exception is ArgumentException or System.ComponentModel.Win32Exception)
        {
            PaperDialog.Tell(this, "无法处理登录凭据", exception.Message, error: true);
        }
    }

    /// <summary>
    /// 删除应用专属的本机保存项，不影响 Windows 或其他程序的凭据。
    /// </summary>
    /// <param name="sender">触发操作的按钮。</param>
    /// <param name="eventArgs">按钮事件参数。</param>
    private void OnDeleteSaved(object? sender, EventArgs eventArgs)
    {
        if (!PaperDialog.Confirm(this, "删除本机登录凭据？",
            "将移除 SideStage 保存的子会话登录凭据。您的 Windows 账户会保留。", "删除凭据"))
        {
            return;
        }

        try
        {
            _credentialStore.Delete();
            _passwordInput.Clear();
            _deleteSavedButton.Enabled = false;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            PaperDialog.Tell(this, "无法删除已保存凭据", exception.Message, error: true);
        }
    }

    /// <summary>
    /// 将输入框密码逐字符复制到 SecureString，再构造一次性凭据对象。
    /// </summary>
    /// <returns>由调用方负责释放的受保护登录凭据。</returns>
    internal static WindowsChildSessionCredentials ReadFields(TextBox account, string domain, TextBox secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Text);
        if (secret.TextLength == 0)
        {
            throw new ArgumentException("请输入 Windows 账户密码。");
        }

        SecureString password = new();
        try
        {
            foreach (char character in secret.Text)
            {
                password.AppendChar(character);
            }

            password.MakeReadOnly();
            return new WindowsChildSessionCredentials(
                account.Text,
                domain,
                password);
        }
        finally
        {
            password.Dispose();
            secret.Clear();
            secret.ClearUndo();
        }
    }
}
