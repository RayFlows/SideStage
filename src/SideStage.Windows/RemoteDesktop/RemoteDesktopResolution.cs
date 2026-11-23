/*
 * 文件名称：RemoteDesktopResolution.cs
 * 文件职责：
 *   描述子会话初始远端桌面的宽高，并集中提供常用分辨率预设与输入校验。
 *
 * 设计边界：
 *   - 分辨率只影响下一次 RDP 连接协商的远端逻辑桌面，不代表本地预览控件大小。
 *   - 预设仅用于方便选择；调用方可以创建 Windows RDP ActiveX 支持范围内的自定义尺寸。
 *   - 本文件不访问 Windows API、显示器或 UI。
 */

using System.Drawing;

namespace SideStage.Windows.RemoteDesktop;

/// <summary>
/// 表示远端子会话逻辑桌面的分辨率。
/// </summary>
public readonly record struct RemoteDesktopResolution
{
    private const int MinimumDimension = 200;
    private const int MaximumDimension = 8192;

    private static readonly IReadOnlyList<RemoteDesktopResolution> CommonResolutionValues =
        Array.AsReadOnly<RemoteDesktopResolution>(
        [
            new(800, 600),
            new(1024, 768),
            new(1280, 720),
            new(1280, 800),
            new(1366, 768),
            new(1440, 900),
            new(1600, 900),
            new(1680, 1050),
            new(1920, 1080),
            new(1920, 1200),
            new(2560, 1440),
            new(2560, 1600),
            new(3440, 1440),
            new(3840, 2160),
        ]);

    /// <summary>
    /// 初始化并验证远端桌面的像素宽高。
    /// </summary>
    /// <param name="width">宽度，单位为像素。</param>
    /// <param name="height">高度，单位为像素。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 任一边小于 200 或大于 8192 像素时抛出。
    /// </exception>
    public RemoteDesktopResolution(int width, int height)
    {
        if (width is < MinimumDimension or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                $"远端桌面宽度必须介于 {MinimumDimension} 和 {MaximumDimension} 像素之间。");
        }

        if (height is < MinimumDimension or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                height,
                $"远端桌面高度必须介于 {MinimumDimension} 和 {MaximumDimension} 像素之间。");
        }

        Width = width;
        Height = height;
    }

    /// <summary>获取远端逻辑桌面的像素宽度。</summary>
    public int Width { get; }

    /// <summary>获取远端逻辑桌面的像素高度。</summary>
    public int Height { get; }

    /// <summary>
    /// 获取常用分辨率预设。该列表只用于 UI 快速选择，自定义尺寸仍由构造函数支持。
    /// </summary>
    public static IReadOnlyList<RemoteDesktopResolution> Common => CommonResolutionValues;

    /// <summary>
    /// 将分辨率转换为绘制与布局 API 使用的尺寸。
    /// </summary>
    /// <returns>包含宽高的像素尺寸。</returns>
    public Size ToSize() => new(Width, Height);

    /// <summary>
    /// 返回适合下拉选项和诊断信息显示的分辨率文本。
    /// </summary>
    /// <returns>“宽 × 高”格式的像素尺寸。</returns>
    public override string ToString() => $"{Width} × {Height}";
}
