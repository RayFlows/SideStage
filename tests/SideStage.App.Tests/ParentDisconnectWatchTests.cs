/*
 * 文件名称：ParentDisconnectWatchTests.cs
 * 文件职责：验证窗口监听的远程断开周期、锁屏排除和开关持久化。
 * 测试边界：通知由测试直接传入，注册表使用本次随机子项，不修改真实用户设置。
 */
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using SideStage.Core.Sessions;

namespace SideStage.App.Tests;

/// <summary>覆盖用户能直接观察到的恢复设置和窗口通知行为。</summary>
[TestClass]
public sealed class ParentDisconnectWatchTests
{
    private static readonly SessionSnapshot Parent = new(new(1), SessionConnectionState.Active, false,
        DateTimeOffset.UtcNow) { IsLocked = false };
    private static readonly SessionId Child = new(2);

    /// <summary>只响应已绑定子会话的本轮断开；重复消息和延迟采样都不能让同一轮再次恢复。</summary>
    [TestMethod]
    public void OneAttemptPerRemoteDisconnect()
    {
        var watch = new ParentDisconnectWatch();
        watch.Observe(Parent, Child);
        watch.Observe(Parent with { ConnectionState = SessionConnectionState.Disconnected }, null);
        Assert.AreEqual(Child, watch.TakeDisconnect());
        watch.Observe(Parent, Child);
        Assert.IsNull(watch.TakeDisconnect());
        watch.OnRemoteConnect();
        watch.Observe(Parent, Child);
        Assert.AreEqual(Child, watch.TakeDisconnect());
    }

    /// <summary>锁屏、撤销绑定及关闭后再开启时，都必须等到下一次活动桌面的核验。</summary>
    [TestMethod]
    public void LockOrWithdrawnTargetDoesNotRecover()
    {
        var watch = new ParentDisconnectWatch();
        watch.Observe(Parent, Child);
        watch.OnLock();
        watch.Observe(Parent, Child);
        Assert.IsNull(watch.TakeDisconnect());
        watch.OnRemoteConnect();
        watch.Observe(Parent with { IsLocked = true }, Child);
        Assert.IsNull(watch.TakeDisconnect());
        watch.OnRemoteConnect();
        watch.Observe(Parent, Child);
        watch.ClearTarget();
        Assert.IsNull(watch.TakeDisconnect());
    }

    /// <summary>首次默认开启，后续读取用户上次明确选择的值。</summary>
    [TestMethod]
    public void PreferenceDefaultsOnAndPersistsBothValues()
    {
        string path = $@"Software\SideStage.Tests\{Guid.NewGuid():N}";
        try
        {
            Assert.IsTrue(ParchmentPreviewForm.ReadRecoveryPreference(path));
            ParchmentPreviewForm.WriteRecoveryPreference(path, false);
            Assert.IsFalse(ParchmentPreviewForm.ReadRecoveryPreference(path));
            ParchmentPreviewForm.WriteRecoveryPreference(path, true);
            Assert.IsTrue(ParchmentPreviewForm.ReadRecoveryPreference(path));
        }
        finally { Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false); }
    }
}
