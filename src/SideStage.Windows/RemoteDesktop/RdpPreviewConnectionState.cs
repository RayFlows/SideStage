/*
 * 文件名称：RdpPreviewConnectionState.cs
 * 文件职责：
 *   定义本机 RDP ActiveX 预览控件的连接状态。
 *
 * 设计边界：
 *   - 该状态描述客户端控件，不代替 Windows 子会话本身的连接状态。
 *   - 控件已连接也必须再次核对子会话编号，才能确认目标会话。
 */

namespace SideStage.Windows.RemoteDesktop;

/// <summary>
/// 表示 RDP 预览控件当前的连接状态。
/// </summary>
public enum RdpPreviewConnectionState
{
    /// <summary>控件没有连接。</summary>
    Disconnected = 0,

    /// <summary>控件已连接到子会话。</summary>
    Connected = 1,

    /// <summary>控件正在建立连接。</summary>
    Connecting = 2,
}
