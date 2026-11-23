/*
 * 文件名称：ChildSessionPreviewSettingsTests.cs
 * 文件职责：
 *   验证分辨率值对象、常用预设及预览设置默认值和输入边界。
 *
 * 测试范围：
 *   - 确认默认设置为 1920×1080、自适应和保持宽高比。
 *   - 确认自定义远端分辨率必须处于 ActiveX 使用的基本尺寸边界内。
 *   - 不创建 ActiveX、不连接或修改 Windows 会话。
 */

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.Core.Tests.RemoteDesktop;

/// <summary>
/// 覆盖远端分辨率和本地预览显示设置的值校验。
/// </summary>
[TestClass]
public sealed class ChildSessionPreviewSettingsTests
{
    /// <summary>
    /// 验证库默认值保留 1080p、自适应缩放及比例锁定。
    /// </summary>
    [TestMethod]
    public void Default_UsesFullHdAdaptiveAndPreservedAspectRatio()
    {
        ChildSessionPreviewSettings settings = ChildSessionPreviewSettings.Default;

        Assert.AreEqual(new RemoteDesktopResolution(1920, 1080), settings.Resolution);
        Assert.AreEqual(PreviewScalingMode.Adaptive, settings.ScalingMode);
        Assert.IsTrue(settings.PreserveAspectRatio);
    }

    /// <summary>
    /// 验证自定义分辨率通过值对象保留，而不要求属于常用预设。
    /// </summary>
    [TestMethod]
    public void Constructor_AcceptsCustomResolutionOutsideCommonPresets()
    {
        RemoteDesktopResolution resolution = new(2048, 1152);

        Assert.AreEqual(new System.Drawing.Size(2048, 1152), resolution.ToSize());
        Assert.IsFalse(RemoteDesktopResolution.Common.Contains(resolution));
    }

    /// <summary>
    /// 验证低于 RDP ActiveX 可接受范围的自定义宽度明确失败。
    /// </summary>
    [TestMethod]
    public void Resolution_RejectsDimensionsBelowMinimum()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RemoteDesktopResolution(199, 1080));
    }

    /// <summary>
    /// 验证高于 RDP ActiveX 可接受范围的自定义高度明确失败。
    /// </summary>
    [TestMethod]
    public void Resolution_RejectsDimensionsAboveMaximum()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RemoteDesktopResolution(1920, 8193));
    }

    /// <summary>
    /// 验证未定义的缩放模式不会进入公开设置模型。
    /// </summary>
    [TestMethod]
    public void Settings_RejectsUnknownScalingMode()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ChildSessionPreviewSettings(
                new RemoteDesktopResolution(1920, 1080),
                (PreviewScalingMode)42));
    }

    /// <summary>
    /// 验证值类型的默认值不能绕过设置模型中的分辨率校验。
    /// </summary>
    [TestMethod]
    public void Settings_RejectsDefaultResolutionValue()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ChildSessionPreviewSettings(default));
    }
}
