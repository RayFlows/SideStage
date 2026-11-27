/*
 * 文件名称：StartupElevationTests.cs
 * 文件职责：验证启动偏好、单次授权、账户一致性和管理员窗口退出后的启动器行为。
 * 验收边界：只使用随机注册表子项和模拟授权进程，不触发系统 UAC。
 */
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace SideStage.App.Tests;

[TestClass]
public sealed class StartupElevationTests
{
    private const string User = "S-1-5-21-1000";

    /// <summary>验证新用户默认请求管理员权限，关闭与重新开启可由另一次启动读取。</summary>
    [TestMethod]
    public void PreferenceSurvivesRestart()
    {
        string path = $@"Software\SideStage.Tests\{Guid.NewGuid():N}";
        try
        {
            Assert.IsTrue(StartupElevation.IsEnabled(path));
            StartupElevation.SetEnabled(path, false);
            Assert.IsFalse(StartupElevation.IsEnabled(path));
            StartupElevation.SetEnabled(path, true);
            Assert.IsTrue(StartupElevation.IsEnabled(path));
        }
        finally { Registry.CurrentUser.DeleteSubKey(path, false); }
    }

    /// <summary>取消授权时继续普通启动，管理员窗口退出后启动器同步结束，不再创建第二个更新窗口。</summary>
    [TestMethod]
    public void InitialLaunchAsksOnceAndRoutesChildExit()
    {
        var calls = new List<bool>();
        StartupRoute Run(bool classic, int exit) => StartupElevation.Choose(classic ? ["--classic"] : [], false, User,
            () => true, (mode, sid) => { calls.Add(mode); Assert.AreEqual(User, sid); return exit; });
        Assert.AreEqual(StartupRoute.Home, Run(false, StartupElevation.DifferentAccountExitCode));
        Assert.AreEqual(StartupRoute.Exit, Run(true, 0));
        Assert.AreEqual(StartupRoute.Exit, Run(false, 0));
        CollectionAssert.AreEqual(new[] { false, true, false }, calls);
    }

    /// <summary>免授权入口和已处理过授权的进程不二次弹窗，换账户授权时退回原窗口。</summary>
    [TestMethod]
    public void OptOutAndGrantedLaunchDoNotPromptAgain()
    {
        StartupRoute Run(string[] args, bool elevated, bool enabled = true) =>
            StartupElevation.Choose(args, elevated, User, () => enabled, (_, _) => throw new AssertFailedException());
        Assert.AreEqual(StartupRoute.Home, Run([], false, false));
        Assert.AreEqual(StartupRoute.Home, Run([], true));
        Assert.AreEqual(StartupRoute.Home, Run(["--standard"], false));
        Assert.AreEqual(StartupRoute.Home, Run(["--authorized-window", User], true));
        Assert.AreEqual(StartupRoute.DifferentAccount, Run(["--authorized-window", User], false));
        Assert.AreEqual(StartupRoute.DifferentAccount, Run(["--authorized-window", User + "1"], true));
    }
}
