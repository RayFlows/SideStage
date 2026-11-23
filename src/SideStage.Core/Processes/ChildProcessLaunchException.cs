/*
 * 文件名称：ChildProcessLaunchException.cs
 * 文件职责：
 *   以统一异常模型报告跨会话启动流程中的失败阶段和原生错误码。
 *
 * 设计边界：
 *   - 错误阶段用于诊断，不允许平台实现吞掉原始错误码后静默降级。
 *   - 原生错误码可为空；非 Win32 失败通过 InnerException 保留原始异常。
 *   - 异常不保存命令行参数，避免错误日志意外泄露调用方传入的数据。
 */

namespace SideStage.Core.Processes;

/// <summary>
/// 表示创建或核验子会话进程失败。
/// </summary>
public sealed class ChildProcessLaunchException : Exception
{
    /// <summary>
    /// 创建包含失败阶段、可选原生错误码和内部异常的启动异常。
    /// </summary>
    /// <param name="stage">失败的稳定阶段名称。</param>
    /// <param name="message">面向诊断的错误摘要。</param>
    /// <param name="nativeErrorCode">可选 Windows 原生错误码。</param>
    /// <param name="innerException">可选的底层异常。</param>
    public ChildProcessLaunchException(
        string stage,
        string message,
        int? nativeErrorCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        Stage = stage;
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>
    /// 获取失败阶段，例如 ValidateChildSession 或 StartTargetProcess。
    /// </summary>
    public string Stage { get; }

    /// <summary>
    /// 获取可用时的 Windows 原生错误码。
    /// </summary>
    public int? NativeErrorCode { get; }
}
