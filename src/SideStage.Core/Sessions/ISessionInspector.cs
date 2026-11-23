/*
 * 文件名称：ISessionInspector.cs
 * 文件职责：
 *   定义桌面分身库观察 Windows 会话的最小接口。
 *
 * 设计边界：
 *   - 该接口只读，不承担会话创建、连接、断开和结束。
 *   - 接口不依赖 UI、进程启动器或业务调度器。
 *   - “没有子会话”通过 null 表示；“查询失败”通过异常表示，二者不能混淆。
 */

namespace SideStage.Core.Sessions;

/// <summary>
/// 提供 Windows 会话能力和状态的只读观察。
/// </summary>
public interface ISessionInspector
{
    /// <summary>
    /// 查询当前进程所属的 Windows 会话。
    /// </summary>
    /// <returns>当前会话的最新快照。</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// 无法读取当前会话状态时抛出。
    /// </exception>
    SessionSnapshot GetCurrentSession();

    /// <summary>
    /// 查询当前父会话是否存在子会话，并在存在时读取其状态。
    /// </summary>
    /// <returns>
    /// 子会话存在时返回快照；当前没有子会话时返回 <see langword="null"/>。
    /// </returns>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// Windows 返回查询失败时抛出。
    /// </exception>
    SessionSnapshot? TryGetChildSession();

    /// <summary>
    /// 查询 Windows 是否启用了子会话能力。
    /// </summary>
    /// <returns>启用时返回 <see langword="true"/>，否则返回 <see langword="false"/>。</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// Windows 无法返回能力开关时抛出。
    /// </exception>
    bool IsChildSessionsEnabled();
}
