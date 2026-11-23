/*
 * 文件名称：IChildSessionTerminator.cs
 * 文件职责：
 *   定义对子会话执行显式注销的契约。
 *
 * 设计边界：
 *   - 注销会结束子会话中的用户进程，属于破坏性操作，调用必须由明确用户动作触发。
 *   - API 不接受任意会话编号，只允许实现层重新查询并注销当前子会话。
 *   - 关闭 RDP 预览连接不等于注销；两种操作由不同对象和方法表达。
 */

namespace SideStage.Core.Sessions;

/// <summary>
/// 提供仅针对当前子会话的显式注销操作。
/// </summary>
public interface IChildSessionTerminator
{
    /// <summary>
    /// 注销当前子会话并结束其中运行的用户进程。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 当前没有已知子会话，或子会话身份校验未通过时抛出。
    /// </exception>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// Windows 注销 API 调用失败时抛出。
    /// </exception>
    void LogoffCurrentChildSession();
}
