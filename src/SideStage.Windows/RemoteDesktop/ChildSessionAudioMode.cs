/*
 * 文件名称：ChildSessionAudioMode.cs
 * 文件职责：
 *   定义子会话预览连接的音频输出策略。
 *
 * 设计边界：
 *   - 只控制 RDP 连接是否播放子会话产生的声音，不修改 Windows 主音量或静音状态。
 *   - 枚举数值与微软 RDP ActiveX 的 AudioRedirectionMode 常量保持一致。
 *   - 默认不播放，避免后台子会话的声音意外输出到主桌面。
 */

namespace SideStage.Windows.RemoteDesktop;

/// <summary>
/// 指定子会话预览连接如何处理子会话音频。
/// </summary>
public enum ChildSessionAudioMode
{
    /// <summary>
    /// 不播放子会话音频，也不转发到运行本库的主桌面。
    /// </summary>
    DoNotPlay = 2,

    /// <summary>
    /// 将子会话音频转发到运行本库的本机音频设备。
    /// </summary>
    RedirectToThisComputer = 0,
}
