/*
 * 文件名称：SessionConnectionState.cs
 * 文件职责：
 *   定义子会话库对 Windows 会话连接状态的稳定抽象。
 *
 * 设计边界：
 *   - 枚举值不直接暴露 Windows 原生枚举，避免上层绑定 P/Invoke 细节。
 *   - Unknown 表示库无法安全确认状态，不等价于空闲、已完成或可继续运行。
 *   - 具体平台实现负责把原生状态映射到本枚举。
 */

namespace SideStage.Core.Sessions;

/// <summary>
/// 表示一个 Windows 会话当前的连接状态。
/// </summary>
public enum SessionConnectionState
{
    /// <summary>无法确认状态。</summary>
    Unknown = 0,

    /// <summary>用户已登录并且会话处于活动状态。</summary>
    Active = 1,

    /// <summary>会话已连接，正在等待或准备进入活动状态。</summary>
    Connected = 2,

    /// <summary>会话正在建立连接。</summary>
    ConnectQuery = 3,

    /// <summary>会话正在被影子连接查看。</summary>
    Shadow = 4,

    /// <summary>用户仍在会话中，但客户端已经断开。</summary>
    Disconnected = 5,

    /// <summary>会话正在等待客户端连接。</summary>
    Idle = 6,

    /// <summary>会话是监听器，不代表有用户登录。</summary>
    Listen = 7,

    /// <summary>会话正在重置。</summary>
    Reset = 8,

    /// <summary>会话因错误处于不可用状态。</summary>
    Down = 9,

    /// <summary>会话正在初始化。</summary>
    Init = 10,
}
