/*
 * 文件名称：ChildDesktopCapture.cs
 * 文件职责：
 *   为上层提供指定分辨率的子会话截图，不读取宿主屏幕或被遮挡的预览窗口。
 *
 * 身份边界：
 *   - 截图前后都确认目标 SessionId 仍是当前父会话下活动的子会话。
 *   - 尺寸不一致、代理失败或会话变化时抛出异常，不返回历史帧或父桌面画面。
 *   - 取图必须留在子会话内的代理进程，像素通过已认证管道传输，不写磁盘缓存。
 *
 * 兼容约束：
 *   - 旧重载继续请求库默认的 1920×1080 尺寸。
 *   - 自定义尺寸重载必须与调用方在 RDP 连接时请求的远端逻辑分辨率一致。
 */
using SideStage.Core.Platform.Windows;
using SideStage.Core.Sessions;
using SideStage.Windows.Processes;

namespace SideStage.Windows.RemoteDesktop;

/// <summary>
/// 按需捕获活动子会话的单帧，与宿主控件是否显示无关。
/// </summary>
public static class ChildDesktopCapture
{
    /// <summary>
    /// 使用库默认的 1920×1080 分辨率请求一张子会话 PNG，保留旧调用方式。
    /// </summary>
    /// <param name="sessionId">上层此前确认的子会话编号。</param>
    /// <param name="cancellationToken">取消等待；不会更改会话状态。</param>
    /// <returns>PNG 文件内容，由调用方决定展示或保存。</returns>
    public static Task<byte[]> CapturePngAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default) =>
        CapturePngAsync(sessionId, ChildSessionPreviewSettings.Default.Resolution, cancellationToken);

    /// <summary>
    /// 按指定的远端逻辑分辨率请求一张 PNG；不新建子会话、不在宿主执行取图。
    /// </summary>
    /// <param name="sessionId">上层此前确认的子会话编号。</param>
    /// <param name="resolution">当前子会话应具有的逻辑桌面尺寸。</param>
    /// <param name="cancellationToken">取消等待；不会更改会话状态。</param>
    /// <returns>PNG 文件内容，由调用方决定展示或保存。</returns>
    /// <exception cref="InvalidOperationException">
    /// 目标子会话状态不活动，或当前屏幕尺寸与请求不符时抛出。
    /// </exception>
    public static async Task<byte[]> CapturePngAsync(
        SessionId sessionId,
        RemoteDesktopResolution resolution,
        CancellationToken cancellationToken)
    {
        WindowsSessionInspector inspector = new();
        WindowsChildSessionProcessLauncher.ValidateTargetChildSession(inspector, sessionId);
        byte[] image = await new WindowsTaskSchedulerChildSessionProcessStarter()
            .CaptureAsync(sessionId, resolution, cancellationToken)
            .ConfigureAwait(false);
        WindowsChildSessionProcessLauncher.ValidateTargetChildSession(inspector, sessionId);
        return image;
    }
}
