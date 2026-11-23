/*
 * 文件名称：ChildSessionAudioMode.cs
 * 文件职责：
 *   定义桌面分身预览连接的音频输出策略。
 *
 * 设计边界：
 *   - 只控制 RDP 连接是否播放分身产生的声音，不修改 Windows 主音量或静音状态。
 *   - 枚举数值与微软 RDP ActiveX 的 AudioRedirectionMode 常量保持一致。
 *   - 默认不播放，避免后台分身的声音意外输出到主桌面。
 */

namespace SideStage.Windows.RemoteDesktop;

/// <summary>
/// 指定分身预览连接如何处理子会话音频。
/// </summary>
public enum ChildSessionAudioMode
{
    /// <summary>
    /// 不播放分身音频，也不转发到运行本库的主桌面。
    /// </summary>
    DoNotPlay = 2,

    /// <summary>
    /// 将分身音频转发到运行本库的本机音频设备。
    /// </summary>
    RedirectToThisComputer = 0,
}
