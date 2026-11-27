/*
 * 文件名称：ChildSessionPreviewPanelTests.cs
 * 文件职责：
 *   验证 RDP 预览面板在 WinForms 构造和布局生命周期中的初始化安全性。
 *
 * 测试边界：
 *   - 测试在 STA 线程中创建真实 WinForms 面板，但不连接或配置 RDP ActiveX。
 *   - 覆盖 WinForms 基类构造期间可能提前触发布局回调的生命周期顺序。
 *   - 验证面板完成构造后仍能为 16:9 预览控件计算有效显示矩形。
 */

using System.Drawing;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.Core.Tests.RemoteDesktop;

/// <summary>
/// 覆盖子会话预览面板的构造期布局和完成初始化后的尺寸布局。
/// </summary>
[TestClass]
public sealed class ChildSessionPreviewPanelTests
{
    /// <summary>
    /// 验证面板构造不会因基类提前触发布局回调而抛出空引用异常。
    /// </summary>
    [STATestMethod]
    public void Constructor_HandlesEarlyLayoutAndInitializesPreviewControl()
    {
        using ChildSessionPreviewPanel panel = new()
        {
            Size = new Size(800, 600),
        };

        Assert.IsNotNull(panel.PreviewControl);
        Assert.AreEqual(
            PreviewViewport.Calculate(
                panel.PreviewControl.Parent!.ClientSize.Width,
                panel.PreviewControl.Parent.ClientSize.Height,
                sourceWidth: 1920,
                sourceHeight: 1080),
            panel.PreviewControl.Bounds);
        Assert.IsTrue(panel.Controls.Contains(panel.PreviewControl.Parent));
    }

    /// <summary>
    /// 验证 1:1 模式按自定义远端分辨率布局，不把它缩放到面板大小。
    /// </summary>
    [STATestMethod]
    public void Constructor_OneToOneUsesNativeResolutionBounds()
    {
        ChildSessionPreviewSettings settings = new(
            new RemoteDesktopResolution(2560, 1440),
            PreviewScalingMode.OneToOne);
        using ChildSessionPreviewPanel panel = new(settings)
        {
            Size = new Size(800, 600),
        };

        Assert.AreEqual(settings, panel.Settings);
        Assert.AreEqual(new Size(2560, 1440), panel.PreviewControl.Size);
    }

    /// <summary>
    /// 验证 1:1 滚动后，面板点击坐标会根据滚动偏移映射回远端原始像素。
    /// </summary>
    [STATestMethod]
    public void TryMapToRemotePoint_OneToOneAccountsForScrollOffset()
    {
        ChildSessionPreviewSettings settings = new(
            new RemoteDesktopResolution(2560, 1440),
            PreviewScalingMode.OneToOne);
        using Form host = new() { ClientSize = new Size(500, 400) };
        using ChildSessionPreviewPanel panel = new(settings) { Dock = DockStyle.Fill };
        host.Controls.Add(panel);
        host.Show();
        Application.DoEvents();

        Panel surface = (Panel)panel.PreviewControl.Parent!;
        surface.AutoScrollPosition = new Point(200, 100);
        Application.DoEvents();

        Assert.AreEqual(
            new Point(210, 110),
            panel.TryMapToRemotePoint(new Point(10, 10)));
    }

    /// <summary>
    /// 验证关闭比例锁定时，预览控件的边界铺满宿主可用区域。
    /// </summary>
    [STATestMethod]
    public void Constructor_AdaptiveWithoutAspectRatioFillsPanel()
    {
        ChildSessionPreviewSettings settings = new(
            new RemoteDesktopResolution(1920, 1080),
            PreviewScalingMode.Adaptive,
            preserveAspectRatio: false);
        using ChildSessionPreviewPanel panel = new(settings)
        {
            Size = new Size(800, 600),
        };

        Assert.AreEqual(
            new Rectangle(Point.Empty, panel.PreviewControl.Parent!.ClientSize),
            panel.PreviewControl.Bounds);
    }

    /// <summary>
    /// 验证连接目标只有通过活动子会话核验后才会显示远端画面。
    /// </summary>
    [STATestMethod]
    public void SetPreviewState_ShowsRemoteContentOnlyAfterSessionVerification()
    {
        using ChildSessionPreviewPanel panel = new()
        {
            Size = new Size(800, 600),
        };
        Control emptyStatePanel = panel.Controls
            .Cast<Control>()
            .Single(control => control != panel.PreviewControl.Parent);

        panel.SetPreviewState(
            RdpPreviewConnectionState.Disconnected,
            hasChildSession: false,
            childSessionsEnabled: true,
            isVerifiedChildSession: false);

        Assert.IsFalse(panel.PreviewControl.Enabled);
        Assert.IsTrue(emptyStatePanel.Visible);

        panel.SetPreviewState(
            RdpPreviewConnectionState.Connected,
            hasChildSession: true,
            childSessionsEnabled: true,
            isVerifiedChildSession: false,
            verificationMessage: "目标会话尚未活动");

        Assert.IsTrue(panel.PreviewControl.Visible);
        Assert.IsFalse(panel.IsPresentingChildSignIn);
        Assert.IsFalse(panel.PreviewControl.Enabled);
        Assert.IsTrue(emptyStatePanel.Visible);
        Assert.IsTrue(
            panel.Controls.GetChildIndex(emptyStatePanel) <
            panel.Controls.GetChildIndex(panel.PreviewControl.Parent!));

        panel.SetPreviewState(
            RdpPreviewConnectionState.Connected,
            hasChildSession: true,
            childSessionsEnabled: true,
            isVerifiedChildSession: true);

        Assert.IsTrue(panel.PreviewControl.Visible);
        Assert.IsTrue(panel.PreviewControl.Enabled);
        Assert.IsFalse(emptyStatePanel.Visible);
    }

    /// <summary>
    /// 模拟用户第一次在未连接的面板上准备控件。断开状态的遮罩应遮住背景，
    /// 同时允许宿主创建 ActiveX 窗口并写入目标属性。
    /// </summary>
    [STATestMethod]
    public void PreparePreview_FromCoveredDisconnectedPanel_CreatesActiveX()
    {
        using Form host = new();
        using ChildSessionPreviewPanel panel = new();
        host.Controls.Add(panel);
        host.Show();
        panel.SetPreviewState(RdpPreviewConnectionState.Disconnected, false, true, false);

        panel.PreviewControl.PreparePreview();

        Assert.IsTrue(panel.PreviewControl.IsHandleCreated);
        Assert.AreEqual(RdpPreviewConnectionState.Disconnected, panel.PreviewControl.GetConnectionState());
    }
}
