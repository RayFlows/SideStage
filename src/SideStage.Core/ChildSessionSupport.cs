/*
 * 文件名称：ChildSessionSupport.cs
 * 文件职责：
 *   1. 暴露桌面分身库对 Windows 子会话能力的最小查询接口。
 *   2. 将 Windows 原生 API 的返回值转换为稳定、可诊断的 .NET 行为。
 *
 * 设计边界：
 *   - 本文件只负责能力查询，不创建会话、不连接会话、不启动进程。
 *   - 本库不依赖任何游戏、脚本工具、调度器、账号或生产配置。
 *   - 查询失败必须抛出包含 Win32 错误码的异常，调用方不得把失败误认为“功能未启用”。
 *
 * 维护要求：
 *   - 修改 P/Invoke 签名时，必须同时核对 Windows SDK 文档和真实 Windows 集成测试。
 *   - 不要在这里加入 UI、日志文件写入或业务重试；这些职责属于上层组件。
 */

using System.ComponentModel;
using SideStage.Core.Platform.Windows;

namespace SideStage.Core;

/// <summary>
/// 提供 Windows 子会话能力的只读查询。
/// </summary>
/// <remarks>
/// 该类型是桌面分身库的基础能力探针，不代表系统中已经存在可连接的子会话。
/// 调用方仍必须在后续流程中独立验证会话编号、连接状态和目标进程归属。
/// </remarks>
public static class ChildSessionSupport
{
    /// <summary>
    /// 查询当前 Windows 是否启用了子会话功能。
    /// </summary>
    /// <returns>
    /// 如果 Windows 成功返回启用状态，则返回 <see langword="true"/> 表示已启用，
    /// 返回 <see langword="false"/> 表示未启用。
    /// </returns>
    /// <exception cref="Win32Exception">
    /// Windows 原生 API 调用失败时抛出；异常中包含原始 Win32 错误码。
    /// </exception>
    /// <remarks>
    /// 此方法是纯查询操作，不会创建、连接、断开或结束任何会话。
    /// “已启用”只表示系统能力开关可用，不表示子会话一定存在，也不表示
    /// 后续的 RDP 承载、输入转发或跨会话启动已经验收通过。
    /// </remarks>
    public static bool IsEnabled()
    {
        return new WindowsSessionInspector().IsChildSessionsEnabled();
    }
}
