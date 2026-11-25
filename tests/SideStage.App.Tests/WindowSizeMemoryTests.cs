/*
 * 文件名称：WindowSizeMemoryTests.cs
 * 文件职责：在真实 WinForms 窗口上验证关闭和重开的尺寸，另行覆盖 DPI 换算和工作区限制。
 * 数据隔离：每次测试使用随机的临时注册表键，避免覆盖用户的窗口偏好。
 */
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace SideStage.App.Tests;

/// <summary>覆盖窗口状态转换中容易被最小化尺寸或最大化尺寸覆盖的还原值。</summary>
[TestClass]
public sealed class WindowSizeMemoryTests
{
    /// <summary>用前一个窗口写下的偏好打开新窗口，核对初始状态及还原后的尺寸。</summary>
    [STATestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ReopenRestoresNormalSizeAndLastVisibleState(bool maximize, bool minimize)
    {
        string path = $@"Software\SideStage.Tests\{Guid.NewGuid():N}";
        try
        {
            using var first = new Form { ShowInTaskbar = false };
            WindowSizeMemory.Attach(first, path, "WindowSize");
            first.Show();
            Size wanted = new(518, 362);
            first.Size = wanted;
            if (maximize) first.WindowState = FormWindowState.Maximized;
            if (minimize) first.WindowState = FormWindowState.Minimized;
            first.Close();

            using var second = new Form { ShowInTaskbar = false };
            WindowSizeMemory.Attach(second, path, "WindowSize");
            second.Show();
            Assert.AreEqual(maximize ? FormWindowState.Maximized : FormWindowState.Normal, second.WindowState);
            second.WindowState = FormWindowState.Normal;
            Assert.AreEqual(wanted, second.Size);
            second.Close();
        }
        finally { Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false); }
    }

    /// <summary>换到双倍缩放的显示器时还原相同的逻辑尺寸，小屏幕上将其限制在可用范围内。</summary>
    [TestMethod]
    public void FitRespectsMonitorDpiAndWorkingArea()
    {
        var saved = new WindowSizeMemory.SavedSize(1200, 700, 96, false);
        Assert.AreEqual(new Size(2400, 1400), saved.Fit(192, new(960, 640), new(3840, 2100)));
        Assert.AreEqual(new Size(1920, 1000), saved.Fit(192, new(960, 640), new(1920, 1000)));
    }
}
