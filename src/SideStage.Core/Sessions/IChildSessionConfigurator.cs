/*
 * 文件名称：IChildSessionConfigurator.cs
 * 文件职责：
 *   定义对子会话系统功能开关进行显式配置的契约。
 *
 * 设计边界：
 *   - 配置开关会改变 Windows 系统状态，不得在库构造、查询或连接时隐式调用。
 *   - 上层必须通过明确的用户操作触发配置，再由实现层报告系统是否接受该操作。
 *   - 查询能力开关仍通过 ISessionInspector 完成，避免读写职责混在同一接口。
 */

namespace SideStage.Core.Sessions;

/// <summary>
/// 显式启用或禁用当前 Windows 用户会话的子会话功能。
/// </summary>
public interface IChildSessionConfigurator
{
    /// <summary>
    /// 设置当前用户会话的子会话功能开关。
    /// </summary>
    /// <param name="enabled">
    /// <see langword="true"/> 表示启用；<see langword="false"/> 表示禁用。
    /// </param>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// Windows 拒绝或无法完成系统设置变更时抛出。
    /// </exception>
    void SetEnabled(bool enabled);
}
