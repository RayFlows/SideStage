/*
 * 文件名称：Program.cs
 * 文件职责：
 *   提供一个不修改系统状态的本机验证入口，用于确认核心库可以读取当前
 *   Windows 子会话能力和调用进程所属的会话编号。
 *
 * 使用边界：
 *   - 本工具不是桌面分身宿主，也不是业务调度器。
 *   - 本工具不会创建、连接、关闭子会话，不会启动游戏或第三方脚本。
 *   - 本工具只用于开发阶段的快速诊断；生产代码不得依赖其输出文本。
 *
 * 退出码约定：
 *   0：查询成功。
 *   1：Windows 原生能力不可读取，或当前运行环境缺少必要组件。
 */

using System.ComponentModel;
using SideStage.Core.Platform.Windows;
using SideStage.Core.Sessions;

namespace SessionProbe;

/// <summary>
/// SessionProbe 的进程入口。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 查询调用进程所在的会话和子会话能力，并将结果写到控制台。
    /// </summary>
    /// <returns>
    /// 查询成功返回 0；发生可预期的 Windows 能力错误时返回 1。
    /// </returns>
    public static int Main()
    {
        try
        {
            WindowsSessionInspector inspector = new();
            SessionSnapshot currentSession = inspector.GetCurrentSession();

            // 输出会话编号和连接状态，便于与 Windows 任务管理器及集成测试记录对照。
            Console.WriteLine($"Current session: {currentSession.Id}");
            Console.WriteLine($"Current connection state: {currentSession.ConnectionState}");
            Console.WriteLine($"Child sessions enabled: {inspector.IsChildSessionsEnabled()}");

            SessionSnapshot? childSession = inspector.TryGetChildSession();
            Console.WriteLine(childSession is null
                ? "Child session: none"
                : $"Child session: {childSession.Id}, state={childSession.ConnectionState}, locked={childSession.IsLocked?.ToString() ?? "unknown"}, ready={childSession.IsDesktopReady}" +
                  (childSession.StateQueryError is int error
                      ? $", stateQueryError={error}"
                      : string.Empty) +
                  (childSession.BasicStateQueryError is int basicError ? $", basicQueryError={basicError}" : string.Empty));

            return 0;
        }
        catch (Exception ex) when (ex is Win32Exception
            or InvalidOperationException
            or InvalidDataException
            or DllNotFoundException
            or EntryPointNotFoundException)
        {
            // 这里只报告查询失败，不尝试修复或静默降级到主桌面。
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
