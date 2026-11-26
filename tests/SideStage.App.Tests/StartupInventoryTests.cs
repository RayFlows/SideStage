/*
 * 文件职责：验证取消扫描方式选择后的配置兼容性，升级时保留用户原先的自动扫描偏好。
 */
using SideStage.App;

namespace SideStage.App.Tests;

[TestClass]
public sealed class StartupInventoryTests
{
    /// <summary>使用旧版真实文件结构，验证自动开关独立于当时选择的扫描方式，重新保存后只写开关。</summary>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public void PreservesLegacyAutoScanPreference(bool onLaunch, bool full)
    {
        var directory = Directory.CreateTempSubdirectory("SideStage-Preferences-");
        string file = Path.Combine(directory.FullName, "preferences.json");
        try
        {
            var store = new StartupInventory(directory.FullName);
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { OnLaunch = onLaunch, Full = full }));
            Assert.AreEqual(onLaunch, store.Preferences().OnLaunch);
            store.SavePreferences(new(!onLaunch));
            Assert.AreEqual(!onLaunch, store.Preferences().OnLaunch);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            Assert.IsFalse(saved.RootElement.TryGetProperty("Full", out _));
        }
        finally
        {
            File.Delete(file);
            directory.Delete();
        }
    }
}
