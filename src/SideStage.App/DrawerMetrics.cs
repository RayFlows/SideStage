/*
 * 文件名称：DrawerMetrics.cs
 * 文件职责：将设置抽屉的设计间距和行高换算为当前显示器上的实际尺寸。
 * 生命周期：首次显示、动态建立凭据页、跨屏及字号变化时，均从原始设计值重新计算。
 */
namespace SideStage.App;

internal static class DrawerMetrics
{
    /// <summary>立即应用尺寸，并在取得实际窗口句柄或显示环境改变后重新测量。</summary>
    internal static void Bind(Control owner, Action measure)
    {
        owner.HandleCreated += (_, _) => measure();
        owner.DpiChangedAfterParent += (_, _) => measure();
        owner.FontChanged += (_, _) => measure();
        measure();
    }

    /// <summary>为文字与编辑器保留上下留白，设计行高是下限，较大的字体可以继续撑开整行。</summary>
    internal static int RowHeight(Control owner, int designHeight, int verticalSpace = 10) =>
        Math.Max(owner.LogicalToDeviceUnits(designHeight),
            TextRenderer.MeasureText("国", owner.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height
            + owner.LogicalToDeviceUnits(verticalSpace));

    /// <summary>按四边各自的原始逻辑像素计算边距，连续跨屏时不会累积舍入误差。</summary>
    internal static Padding Space(Control owner, int left, int top, int right, int bottom) =>
        new(owner.LogicalToDeviceUnits(left), owner.LogicalToDeviceUnits(top),
            owner.LogicalToDeviceUnits(right), owner.LogicalToDeviceUnits(bottom));

    /// <summary>把分组、标题和说明的外间距绑定到各自的显示器。</summary>
    internal static void Margin(Control owner, int left, int top, int right, int bottom) =>
        Bind(owner, () => owner.Margin = Space(owner, left, top, right, bottom));
}
