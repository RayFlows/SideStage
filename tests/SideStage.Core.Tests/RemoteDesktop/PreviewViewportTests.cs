/*
 * 文件名称：PreviewViewportTests.cs
 * 文件职责：
 *   验证预览面板的几何计算始终完整显示源画面并居中留边。
 *
 * 测试范围：
 *   - 验证宽屏和窄屏容器中的画面矩形。
 *   - 验证零尺寸与非法尺寸的输入行为。
 *   - 不创建 ActiveX、不连接或修改 Windows 会话。
 */

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.Core.Tests.RemoteDesktop;

/// <summary>
/// 覆盖固定比例预览视口的边界行为。
/// </summary>
[TestClass]
public sealed class PreviewViewportTests
{
    /// <summary>
    /// 验证宽于 16:9 的容器会在左右方向居中留边。
    /// </summary>
    [TestMethod]
    public void Calculate_WideContainer_LetterboxesHorizontally()
    {
        System.Drawing.Rectangle viewport = PreviewViewport.Calculate(
            containerWidth: 1000,
            containerHeight: 600,
            sourceWidth: 1920,
            sourceHeight: 1080);

        Assert.AreEqual(new System.Drawing.Rectangle(0, 19, 1000, 562), viewport);
    }

    /// <summary>
    /// 验证窄于 16:9 的容器会在上下方向居中留边。
    /// </summary>
    [TestMethod]
    public void Calculate_TallContainer_LetterboxesVertically()
    {
        System.Drawing.Rectangle viewport = PreviewViewport.Calculate(
            containerWidth: 800,
            containerHeight: 600,
            sourceWidth: 1920,
            sourceHeight: 1080);

        Assert.AreEqual(new System.Drawing.Rectangle(0, 75, 800, 450), viewport);
    }

    /// <summary>
    /// 验证容器暂时没有可用空间时返回空矩形，不进行负数布局。
    /// </summary>
    [TestMethod]
    public void Calculate_ZeroSizedContainer_ReturnsEmptyRectangle()
    {
        Assert.AreEqual(
            System.Drawing.Rectangle.Empty,
            PreviewViewport.Calculate(0, 400, 1920, 1080));
    }

    /// <summary>
    /// 验证源画面不能使用零高度，否则比例计算没有定义。
    /// </summary>
    [TestMethod]
    public void Calculate_ZeroSizedSource_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => PreviewViewport.Calculate(800, 600, 1920, 0));
    }

    /// <summary>
    /// 验证宿主坐标会去除上下留边后按整数比例映射到 1920×1080。
    /// </summary>
    [TestMethod]
    public void ToRemotePoint_MapsInsideViewportAndRejectsLetterbox()
    {
        System.Drawing.Size host = new(800, 600);

        Assert.IsNull(PreviewViewport.ToRemotePoint(new System.Drawing.Point(10, 20), host));
        Assert.AreEqual(
            new System.Drawing.Point(960, 540),
            PreviewViewport.ToRemotePoint(new System.Drawing.Point(400, 300), host));
    }

    /// <summary>
    /// 验证关闭宽高比锁定时，自适应预览填满容器并允许分别缩放宽高。
    /// </summary>
    [TestMethod]
    public void Calculate_AdaptiveWithoutAspectRatio_FillsContainer()
    {
        System.Drawing.Rectangle viewport = PreviewViewport.Calculate(
            containerWidth: 1000,
            containerHeight: 600,
            sourceWidth: 1920,
            sourceHeight: 1080,
            preserveAspectRatio: false);

        Assert.AreEqual(new System.Drawing.Rectangle(0, 0, 1000, 600), viewport);
    }

    /// <summary>
    /// 验证任意远端分辨率的坐标都能按实际铺满尺寸映射，且最后一像素不越界。
    /// </summary>
    [TestMethod]
    public void ToRemotePoint_CustomResolutionMapsAcrossFilledViewport()
    {
        System.Drawing.Point? remotePoint = PreviewViewport.ToRemotePoint(
            new System.Drawing.Point(999, 599),
            new System.Drawing.Size(1000, 600),
            new System.Drawing.Size(2560, 1440),
            PreviewScalingMode.Adaptive,
            preserveAspectRatio: false);

        Assert.AreEqual(new System.Drawing.Point(2557, 1437), remotePoint);
    }

    /// <summary>
    /// 验证 1:1 模式不会缩放远端坐标，并拒绝原始画布以外的位置。
    /// </summary>
    [TestMethod]
    public void ToRemotePoint_OneToOnePreservesPixelCoordinates()
    {
        System.Drawing.Size resolution = new(2560, 1440);

        Assert.AreEqual(
            new System.Drawing.Point(1500, 800),
            PreviewViewport.ToRemotePoint(
                new System.Drawing.Point(1500, 800),
                new System.Drawing.Size(1920, 1080),
                resolution,
                PreviewScalingMode.OneToOne));
        Assert.IsNull(
            PreviewViewport.ToRemotePoint(
                new System.Drawing.Point(2560, 20),
                resolution,
                resolution,
                PreviewScalingMode.OneToOne));
    }
}
