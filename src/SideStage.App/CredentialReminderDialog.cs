/*
 * 文件名称：CredentialReminderDialog.cs
 * 文件职责：在用户尚未保存凭据时，使用应用的纸面配色显示登录建议和可选操作。
 * 交互约定：窗体只返回本次选择，持久化交给凭据引导。回车默认进入配置，Esc 和关闭按钮取消。
 */
namespace SideStage.App;

internal sealed class CredentialReminderDialog : PaperDialog
{
    private readonly CheckBox _remember = new()
    {
        Text = "不再提示（以后直接连接）", AutoSize = true, FlatStyle = FlatStyle.Flat,
        Cursor = Cursors.Hand, Margin = new Padding(0, 16, 0, 0)
    };

    /// <summary>构建与更新窗口一致的模态提醒。正文随列宽折行，各行按字体高度排版。</summary>
    private CredentialReminderDialog()
        : base("连接子会话", "建议配置登录凭据",
            "使用 Windows 账户密码连接子会话通常更稳定。如果您平时使用 PIN 登录，选择「直接连接」时，Windows 可能会要求您重新登录。")
    {
        AddOptions(_remember);
        var configure = AddAction("配置登录凭据", DialogResult.OK, accent: true);
        AddAction("直接连接", DialogResult.Yes);
        AcceptButton = configure;
        CancelButton = AddAction("取消", DialogResult.Cancel);
        ActiveControl = configure;
    }

    /// <summary>在所属窗口中央显示提醒，返回按钮与勾选状态，让两个主窗口共用同一套选择语义。</summary>
    internal static (CredentialChoice Choice, bool Remember) Ask(IWin32Window owner)
    {
        using var dialog = new CredentialReminderDialog();
        var choice = dialog.ShowDialog(owner) switch
        {
            DialogResult.OK => CredentialChoice.Configure,
            DialogResult.Yes => CredentialChoice.Direct,
            _ => CredentialChoice.Cancel
        };
        return (choice, dialog._remember.Checked);
    }

}
