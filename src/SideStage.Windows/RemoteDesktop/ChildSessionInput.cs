/*
 * 文件名称：ChildSessionInput.cs
 * 文件职责：
 *   向已经通过 WTS 身份核验的 RDP 子会话预览发送最小鼠标和键盘消息。
 *
 * 安全约束：
 *   - 每次输入前检查控件 Connected 状态并重新核验当前活动子会话。
 *   - 坐标是当前远端逻辑像素，不接受宿主屏幕坐标，避免 DPI 和留边误差。
 *   - 未核验、断开或超出当前分辨率的输入都会失败，不回退到父窗口或 SendInput。
 *   - 本类型只发送到 RDP ActiveX 子窗口，不向系统全局注入输入。
 */

using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SideStage.Windows.RemoteDesktop;

/// <summary>
/// 对一个已经核验的子会话预览执行受控输入。
/// </summary>
public sealed class ChildSessionInput
{
    private const uint WindowMessageMouseMove = 0x0200;
    private const uint WindowMessageLeftButtonDown = 0x0201;
    private const uint WindowMessageLeftButtonUp = 0x0202;
    private const uint WindowMessageRightButtonDown = 0x0204;
    private const uint WindowMessageRightButtonUp = 0x0205;
    private const uint WindowMessageMiddleButtonDown = 0x0207;
    private const uint WindowMessageMiddleButtonUp = 0x0208;
    private const uint WindowMessageKeyDown = 0x0100;
    private const uint WindowMessageKeyUp = 0x0101;
    private const nuint MouseLeftButton = 0x0001;
    private const nuint MouseRightButton = 0x0002;
    private const nuint MouseMiddleButton = 0x0010;

    private readonly ChildSessionPreviewControl _previewControl;

    /// <summary>
    /// 将输入控制器绑定到一个 RDP 预览控件。
    /// </summary>
    /// <param name="previewControl">需要接收输入的预览控件。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="previewControl"/> 为 null 时抛出。
    /// </exception>
    public ChildSessionInput(ChildSessionPreviewControl previewControl)
    {
        _previewControl = previewControl
            ?? throw new ArgumentNullException(nameof(previewControl));
    }

    /// <summary>
    /// 在当前远端逻辑分辨率中点击一个鼠标位置。
    /// </summary>
    /// <param name="remotePoint">远端坐标，左上角为 (0,0)。</param>
    /// <param name="button">需要点击的鼠标按钮。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 坐标不在当前远端分辨率范围内时抛出。
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 预览未连接或当前子会话身份无法重新核验时抛出。
    /// </exception>
    public void Click(Point remotePoint, MouseButtons button = MouseButtons.Left)
    {
        EnsureVerifiedConnected();
        ValidateRemotePoint(remotePoint, _previewControl.Settings.Resolution);

        (uint downMessage, uint upMessage, nuint buttonFlag) = button switch
        {
            MouseButtons.Left => (WindowMessageLeftButtonDown, WindowMessageLeftButtonUp, MouseLeftButton),
            MouseButtons.Right => (WindowMessageRightButtonDown, WindowMessageRightButtonUp, MouseRightButton),
            MouseButtons.Middle => (WindowMessageMiddleButtonDown, WindowMessageMiddleButtonUp, MouseMiddleButton),
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, "只支持左键、右键和中键。"),
        };

        nint lParam = MakeMousePoint(remotePoint);
        SendWindowMessage(WindowMessageMouseMove, buttonFlag, lParam);
        SendWindowMessage(downMessage, buttonFlag, lParam);
        SendWindowMessage(upMessage, 0, lParam);
    }

    /// <summary>
    /// 向远端活动窗口发送一个虚拟键按下和释放消息。
    /// </summary>
    /// <param name="key">Windows 虚拟键值。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="key"/> 不是有效的虚拟键值时抛出。
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 预览未连接或当前子会话身份无法重新核验时抛出。
    /// </exception>
    public void Press(Keys key)
    {
        EnsureVerifiedConnected();

        if (key is Keys.None or > Keys.Packet)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "无效的 Windows 虚拟键值。");
        }

        SendWindowMessage(WindowMessageKeyDown, (nuint)key, 0);
        SendWindowMessage(WindowMessageKeyUp, (nuint)key, 0);
    }

    /// <summary>
    /// 检查 RDP 控件连接并重新核验当前子会话身份。
    /// </summary>
    private void EnsureVerifiedConnected()
    {
        if (_previewControl.GetConnectionState() != RdpPreviewConnectionState.Connected)
        {
            throw new InvalidOperationException("预览未连接，拒绝发送子会话输入。");
        }

        _ = _previewControl.VerifyConnectedChildSession();
    }

    /// <summary>
    /// 验证坐标位于当前远端桌面的像素范围内。
    /// </summary>
    /// <param name="remotePoint">待检查的远端坐标。</param>
    /// <param name="resolution">当前远端桌面的宽高。</param>
    private static void ValidateRemotePoint(
        Point remotePoint,
        RemoteDesktopResolution resolution)
    {
        if (remotePoint.X < 0
            || remotePoint.Y < 0
            || remotePoint.X >= resolution.Width
            || remotePoint.Y >= resolution.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(remotePoint),
                remotePoint,
                $"远端坐标必须位于 {resolution} 画布内。");
        }
    }

    /// <summary>
    /// 将两个 16 位坐标压入 Windows 鼠标消息的 LPARAM。
    /// </summary>
    /// <param name="point">已经验证的远端坐标。</param>
    /// <returns>Windows LPARAM 坐标值。</returns>
    private static nint MakeMousePoint(Point point)
    {
        return (nint)((point.Y << 16) | (point.X & 0xFFFF));
    }

    /// <summary>
    /// 向 RDP ActiveX 的窗口句柄发送消息，失败时保留 Win32 错误码。
    /// </summary>
    /// <param name="message">Windows 窗口消息。</param>
    /// <param name="wParam">消息的 WPARAM。</param>
    /// <param name="lParam">消息的 LPARAM。</param>
    private void SendWindowMessage(uint message, nuint wParam, nint lParam)
    {
        _ = SendMessage(_previewControl.Handle, message, wParam, lParam);
    }

    /// <summary>
    /// 向指定窗口同步发送消息。
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SendMessage(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);
}
