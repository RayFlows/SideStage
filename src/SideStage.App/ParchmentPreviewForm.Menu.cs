/*
 * 文件名称：ParchmentPreviewForm.Menu.cs
 * 文件职责：通过右键标题栏或系统快捷键访问高级设置、断开和注销命令。
 * 消息边界：只处理本窗口注册的四个命令，窗口移动、调整尺寸和关闭继续使用系统行为。
 * 会话边界：菜单复用窗口已有的操作和确认逻辑，状态读取失败时禁用断开与注销。
 */

using System.Runtime.InteropServices;

namespace SideStage.App;

internal sealed partial class ParchmentPreviewForm
{
    // 系统会使用低四位传递内部标志，自定义命令按 16 对齐且保留在 0xF000 以下。
    private const int SettingsCommand = 0x1200;
    private const int DisconnectCommand = 0x1220;
    private const int LogoffCommand = 0x1230;
    private const int AboutCommand = 0x1240;
    private const int WindowSystemCommand = 0x0112;
    private const int InitMenuPopup = 0x0117;
    private const int NonClientRightUp = 0x00a5;
    private const uint MenuSeparator = 0x0800;
    private const uint MenuDisabled = 0x0001;

    /// <summary>在本窗口独有的系统菜单中增加会话操作，每次创建新句柄时调用一次。</summary>
    private void InstallSessionMenu()
    {
        IntPtr menu = GetSystemMenu(Handle, false);
        if (menu == IntPtr.Zero) return;
        AppendMenu(menu, MenuSeparator, 0, null);
        AppendMenu(menu, 0, SettingsCommand, "高级设置(&P)");
        AppendMenu(menu, MenuDisabled, DisconnectCommand, "断开连接");
        AppendMenu(menu, MenuDisabled, LogoffCommand, "注销子会话…");
        AppendMenu(menu, MenuSeparator, 0, null);
        AppendMenu(menu, 0, AboutCommand, $"关于 {Program.DisplayName}…");
    }

    /// <summary>在菜单弹出时同步状态，执行自定义命令后消费消息，避免送入 RDP。</summary>
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x02B1) OnSessionChange(message.WParam, message.LParam); // WM_WTSSESSION_CHANGE
        if (HandleWindowFrameMessage(ref message)) return;
        if (message.Msg == NonClientRightUp && message.WParam == HitCaption)
        {
            long point = message.LParam.ToInt64();
            ShowWindowMenu(new Point(unchecked((short)point), unchecked((short)(point >> 16))));
            return;
        }
        if (message.Msg == InitMenuPopup && message.WParam == GetSystemMenu(Handle, false))
        {
            EnableMenuItem(message.WParam, DisconnectCommand, _canDisconnect ? 0 : MenuDisabled);
            EnableMenuItem(message.WParam, LogoffCommand, _canLogoff ? 0 : MenuDisabled);
        }
        if (message.Msg == WindowSystemCommand)
        {
            switch (message.WParam.ToInt64() & 0xfff0)
            {
                case SettingsCommand:
                    ToggleSettings(this, EventArgs.Empty);
                    return;
                case DisconnectCommand:
                    if (_canDisconnect) DisconnectPreview(this, EventArgs.Empty);
                    return;
                case LogoffCommand:
                    if (_canLogoff) LogoffChildSession(this, EventArgs.Empty);
                    return;
                case AboutCommand:
                    MessageBox.Show(this, $"{Program.DisplayName}\n\n本程序使用 HarmonyOS Sans SC 字体。\n" +
                        "Copyright 2021 Huawei Device Co., Ltd.", $"关于 {Program.DisplayName}", MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
            }
        }
        base.WndProc(ref message);
    }

    /// <summary>在指定屏幕位置展开 Windows 自带的菜单，将选中的操作交回统一的系统命令处理入口。</summary>
    private void ShowWindowMenu(Point screenPoint)
    {
        IntPtr menu = GetSystemMenu(Handle, false);
        if (menu == IntPtr.Zero) return;
        const uint returnCommand = 0x0100, allowRightButton = 0x0002;
        int command = TrackPopupMenuEx(menu, returnCommand | allowRightButton,
            screenPoint.X, screenPoint.Y, Handle, IntPtr.Zero);
        if (command != 0)
            PostMessage(Handle, WindowSystemCommand, command, 0);
    }

    /// <summary>显示已有系统菜单，返回用户选择的命令编号。取消时返回零。</summary>
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr options);

    /// <summary>在菜单退出自己的消息循环后，再由窗体执行命令。</summary>
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr window, uint message, nint wParam, nint lParam);

    /// <summary>向 Windows 请求此窗口的系统菜单，句柄所有权保留在系统。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr window, bool revert);

    /// <summary>向系统菜单追加一个 Unicode 文本命令或分隔线。</summary>
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);

    /// <summary>按命令编号控制原生菜单项是否可选。</summary>
    [DllImport("user32.dll")]
    private static extern int EnableMenuItem(IntPtr menu, uint id, uint flags);
}
