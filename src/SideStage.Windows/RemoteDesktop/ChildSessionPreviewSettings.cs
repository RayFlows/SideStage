/*
 * 文件名称：ChildSessionPreviewSettings.cs
 * 文件职责：
 *   定义远端逻辑分辨率、本地预览缩放模式及比例锁定策略。
 *
 * 默认策略：
 *   - 请求 1920×1080 的远端桌面。
 *   - 本地预览自适应控件尺寸。
 *   - 自适应时保持远端画面的宽高比并在多余区域留边。
 *
 * 生命周期边界：
 *   - 远端分辨率在下一次 RDP 连接建立时生效，连接期间不可修改。
 *   - 缩放模式和比例锁定只影响宿主内的显示；RDP 支持在连接期间切换缩放模式。
 */

namespace SideStage.Windows.RemoteDesktop;

/// <summary>
/// 指定远端像素与本地预览控件之间的缩放策略。
/// </summary>
public enum PreviewScalingMode
{
    /// <summary>按远端像素一比一显示；空间不足时由预览面板滚动查看。</summary>
    OneToOne,

    /// <summary>将远端桌面缩放到本地预览控件区域。</summary>
    Adaptive,
}

/// <summary>
/// 保存连接预览使用的远端分辨率和本地显示选项。
/// </summary>
public sealed record ChildSessionPreviewSettings
{
    /// <summary>库提供的默认预览设置。</summary>
    public static ChildSessionPreviewSettings Default { get; } = new(
        new RemoteDesktopResolution(1920, 1080),
        PreviewScalingMode.Adaptive,
        preserveAspectRatio: true);

    /// <summary>
    /// 创建一组通过校验的分辨率和显示设置。
    /// </summary>
    /// <param name="resolution">下一次连接请求的远端桌面尺寸。</param>
    /// <param name="scalingMode">本地预览使用 1:1 还是自适应缩放。</param>
    /// <param name="preserveAspectRatio">自适应显示时是否保持远端画面的宽高比。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 使用了未定义的缩放模式时抛出。
    /// </exception>
    public ChildSessionPreviewSettings(
        RemoteDesktopResolution resolution,
        PreviewScalingMode scalingMode = PreviewScalingMode.Adaptive,
        bool preserveAspectRatio = true)
    {
        if (resolution.Width is < 200 or > 8192 || resolution.Height is < 200 or > 8192)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolution),
                resolution,
                "远端桌面分辨率必须通过 RemoteDesktopResolution 的宽高范围校验。");
        }

        if (!Enum.IsDefined(scalingMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scalingMode),
                scalingMode,
                "不支持指定的预览缩放模式。");
        }

        Resolution = resolution;
        ScalingMode = scalingMode;
        PreserveAspectRatio = preserveAspectRatio;
    }

    /// <summary>获取下一次连接请求的远端逻辑分辨率。</summary>
    public RemoteDesktopResolution Resolution { get; }

    /// <summary>获取当前预览缩放策略。</summary>
    public PreviewScalingMode ScalingMode { get; }

    /// <summary>获取自适应显示时是否保持宽高比；1:1 模式下该项不参与布局。</summary>
    public bool PreserveAspectRatio { get; }
}
