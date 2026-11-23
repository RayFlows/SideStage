/*
 * 文件名称：ChildProcessState.cs
 * 文件职责：
 *   定义桌面分身库对受管子会话进程的稳定生命周期状态。
 *
 * 设计边界：
 *   - 枚举只描述由本库成功启动并持有句柄的单个进程，不代表整个任务状态。
 *   - 关闭预览不会改变进程状态；注销子会话由 Windows 结束其中的进程。
 *   - 进程状态只能由管理句柄重新观察得到，不根据启动请求推断。
 */

namespace SideStage.Core.Processes;

/// <summary>
/// 表示一个由桌面分身库启动的进程的生命周期状态。
/// </summary>
public enum ChildProcessState
{
    /// <summary>进程正在运行。</summary>
    Running = 1,

    /// <summary>进程已自行退出。</summary>
    Exited = 2,

    /// <summary>进程已由管理句柄显式终止。</summary>
    Terminated = 3,
}
