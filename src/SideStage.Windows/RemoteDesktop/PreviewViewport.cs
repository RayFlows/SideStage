/*
 * 文件名称：PreviewViewport.cs
 * 文件职责：
 *   以纯几何计算确定远端预览画面在宿主区域中的显示矩形并换算点击坐标。
 *
 * 设计边界：
 *   - 不创建控件、不读取屏幕、不执行 DPI 转换或输入注入。
 *   - 自适应模式可选择保持比例留边或填满容器；1:1 模式按远端像素原样映射。
 *   - 所有尺寸使用调用方提供的同一坐标单位。
 */

using System.Drawing;

namespace SideStage.Windows.RemoteDesktop;

/// <summary>
/// 计算预览画面在宿主客户区中的显示位置、尺寸及远端坐标映射。
/// </summary>
public static class PreviewViewport
{
    /// <summary>把宿主客户区中的 Full HD 预览点映射到远端像素，作为兼容旧调用方的便捷重载。</summary>
    /// <param name="point">与宿主大小处于同一 DPI 坐标系的客户区位置。</param>
    /// <param name="host">显示预览的客户区大小。</param>
    /// <returns>远端像素位置；点在留边或客户区为空时返回空。</returns>
    public static Point? ToRemotePoint(Point point, Size host)
    {
        return ToRemotePoint(
            point,
            host,
            new Size(1920, 1080),
            PreviewScalingMode.Adaptive,
            preserveAspectRatio: true);
    }

    /// <summary>
    /// 将宿主控件客户区中的点映射到指定远端分辨率；留边、越界及空画面位置不产生输入。
    /// </summary>
    /// <param name="point">控件客户区中的点。</param>
    /// <param name="host">远端画面所在控件的尺寸。</param>
    /// <param name="remoteResolution">远端逻辑桌面尺寸。</param>
    /// <param name="scalingMode">控件使用的缩放模式。</param>
    /// <param name="preserveAspectRatio">自适应模式是否保持远端宽高比。</param>
    /// <returns>远端像素坐标；点不可映射时返回空。</returns>
    public static Point? ToRemotePoint(
        Point point,
        Size host,
        Size remoteResolution,
        PreviewScalingMode scalingMode,
        bool preserveAspectRatio = true)
    {
        if (remoteResolution.Width <= 0 || remoteResolution.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(remoteResolution),
                "远端桌面尺寸必须大于零。");
        }

        if (!Enum.IsDefined(scalingMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scalingMode),
                scalingMode,
                "不支持指定的预览缩放模式。");
        }

        if (scalingMode == PreviewScalingMode.OneToOne)
        {
            return point.X >= 0 && point.Y >= 0
                && point.X < remoteResolution.Width
                && point.Y < remoteResolution.Height
                ? point
                : null;
        }

        Rectangle view = Calculate(
            host.Width,
            host.Height,
            remoteResolution.Width,
            remoteResolution.Height,
            preserveAspectRatio);
        if (!view.Contains(point))
        {
            return null;
        }

        return new Point(
            (int)((long)(point.X - view.X) * remoteResolution.Width / view.Width),
            (int)((long)(point.Y - view.Y) * remoteResolution.Height / view.Height));
    }

    /// <summary>
    /// 在容器内部计算一个完整显示源画面的居中等比矩形。
    /// </summary>
    /// <param name="containerWidth">容器客户区宽度。</param>
    /// <param name="containerHeight">容器客户区高度。</param>
    /// <param name="sourceWidth">源画面宽度。</param>
    /// <param name="sourceHeight">源画面高度。</param>
    /// <param name="preserveAspectRatio">是否等比缩放并居中留边；false 时填满容器。</param>
    /// <returns>
    /// 容器坐标系中的居中显示矩形。若容器任一边为零，则返回零尺寸矩形。
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 任一尺寸为负数，或源画面宽高为零时抛出。
    /// </exception>
    public static Rectangle Calculate(
        int containerWidth,
        int containerHeight,
        int sourceWidth,
        int sourceHeight,
        bool preserveAspectRatio = true)
    {
        if (containerWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(containerWidth));
        }

        if (containerHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(containerHeight));
        }

        if (sourceWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        }

        if (sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        }

        if (containerWidth == 0 || containerHeight == 0)
        {
            return Rectangle.Empty;
        }

        if (!preserveAspectRatio)
        {
            return new Rectangle(0, 0, containerWidth, containerHeight);
        }

        double scale = Math.Min(
            (double)containerWidth / sourceWidth,
            (double)containerHeight / sourceHeight);

        int viewportWidth = Math.Clamp(
            (int)Math.Floor(sourceWidth * scale),
            1,
            containerWidth);
        int viewportHeight = Math.Clamp(
            (int)Math.Floor(sourceHeight * scale),
            1,
            containerHeight);

        // 整数像素取整后仍保持源画面的宽高比例，并将多余空间均匀留在两侧。
        int left = (containerWidth - viewportWidth) / 2;
        int top = (containerHeight - viewportHeight) / 2;

        return new Rectangle(left, top, viewportWidth, viewportHeight);
    }
}
