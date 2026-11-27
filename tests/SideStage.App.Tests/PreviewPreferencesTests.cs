/*
 * 文件职责：验证桌面应用能够跨窗口恢复画面和声音偏好。
 * 隔离约定：使用临时注册表键模拟不同窗口的读写顺序。
 */
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App.Tests;

/// <summary>覆盖新安装、重新打开窗口、损坏配置与两个窗口同时修改偏好的场景。</summary>
[TestClass]
public sealed class PreviewPreferencesTests
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private string Key => $@"Software\SideStage.Tests\{_id}";

    /// <summary>清除本次用例创建的临时用户偏好。</summary>
    [TestCleanup]
    public void CleanUp() => Registry.CurrentUser.DeleteSubKey(Key, false);

    /// <summary>两个窗口分别改分辨率和宽高比时，后写入的一方只能更新自己动过的选项。</summary>
    [TestMethod]
    public void StaleWindowPreservesOtherWindowsResolution()
    {
        var first = new PreviewPreferences(Key);
        var stale = new PreviewPreferences(Key);
        var resolution = new RemoteDesktopResolution(1728, 972);
        Assert.IsTrue(first.SaveDisplay(new(resolution), true));
        Assert.IsTrue(stale.SaveDisplay(new(stale.Display.Resolution, preserveAspectRatio: false), false));
        var loaded = new PreviewPreferences(Key);
        Assert.AreEqual(resolution, loaded.Display.Resolution);
        Assert.IsTrue(loaded.CustomResolution);
        Assert.IsFalse(loaded.Display.PreserveAspectRatio);
    }

    /// <summary>缺失、无效或超出范围的显示记录使用默认值，已保存的声音设置仍可恢复。</summary>
    [TestMethod]
    [DataRow("broken")]
    [DataRow("{\"Width\":9000,\"Height\":600,\"Custom\":true}")]
    public void DamagedDisplayDoesNotResetAudio(string invalid)
    {
        var initial = new PreviewPreferences(Key);
        Assert.AreEqual(ChildSessionPreviewSettings.Default, initial.Display);
        Assert.AreEqual(ChildSessionAudioMode.DoNotPlay, initial.Audio);
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("PreviewResolution", invalid);
        key.SetValue("PreviewScaling", 77);
        key.SetValue("PreviewMuted", 0);
        var loaded = new PreviewPreferences(Key);
        Assert.AreEqual(ChildSessionPreviewSettings.Default, loaded.Display);
        Assert.AreEqual(ChildSessionAudioMode.RedirectToThisComputer, loaded.Audio);
    }
}
