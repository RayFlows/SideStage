/*
 * 文件职责：在临时目录与独立的富文本控件中核对活动日志保留策略。
 * 验证范围：旧版命名、多窗口占用、超时与配额回收、分卷、更新诊断与复制选区。
 */
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.App;

namespace SideStage.App.Tests;

[TestClass]
public sealed class ActivityLogTests
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("SideStage.LogTest-");
    private readonly DateTime _now = DateTime.UtcNow;

    /// <summary>只移除本用例创建的临时数据。残留占用会使测试失败。</summary>
    [TestCleanup]
    public void Cleanup() => _root.Delete(true);

    /// <summary>生成与首版完全相同的文件名，并设置确定的大小和历史写入时间。</summary>
    private string Historical(int days, int size = 1, DirectoryInfo? parent = null)
    {
        string path = Path.Combine((parent ?? _root).FullName, $"preview-20250101-010000-{Guid.NewGuid():N}.log");
        using (var stream = File.Create(path)) stream.SetLength(size);
        File.SetLastWriteTimeUtc(path, _now.AddDays(-days));
        return path;
    }

    /// <summary>过期卷和超总容量的最旧卷应被淘汰，而用户文件、嵌套目录和较新分卷仍保留。</summary>
    [TestMethod]
    public void PrunesOwnedOldestFilesFirst()
    {
        string expired = Historical(15);
        string overBudget = Historical(10, ActivityLogStore.TotalBytes);
        string recent = Historical(2);
        string nested = Historical(20, parent: _root.CreateSubdirectory("备份"));
        string unrelated = Path.Combine(_root.FullName, "preview-manual.log");
        File.WriteAllText(unrelated, "保留");
        File.SetLastWriteTimeUtc(unrelated, _now.AddDays(-30));
        using var store = new ActivityLogStore(_root.FullName);
        store.Append("本次启动", _now);
        Assert.IsFalse(File.Exists(expired));
        Assert.IsFalse(File.Exists(overBudget));
        Assert.IsTrue(File.Exists(recent) && File.Exists(nested) && File.Exists(unrelated));
    }

    /// <summary>大量小文件只触发数量限制。活跃窗口继续写原卷，并允许普通文本读取。</summary>
    [TestMethod]
    public void PreservesActiveWriterUnderCountPressure()
    {
        using var first = new ActivityLogStore(_root.FullName);
        first.Append("第一条", _now);
        string active = _root.GetFiles().Single().FullName;
        File.SetLastWriteTimeUtc(active, _now.AddDays(-20));
        for (int i = 0; i < ActivityLogStore.MaxFiles; i++) Historical(1);
        using var second = new ActivityLogStore(_root.FullName);
        second.Append("第二个窗口", _now);
        Assert.HasCount(ActivityLogStore.MaxFiles, _root.GetFiles());
        first.Append("继续写入", _now);
        CollectionAssert.AreEqual(new[] { "第一条", "继续写入" }, File.ReadAllLines(active));
        first.Dispose();
        File.SetLastWriteTimeUtc(active, _now.AddDays(-20));
        second.Maintain(_now);
        Assert.IsFalse(File.Exists(active));
    }

    /// <summary>按实际 UTF-8 字节数换卷，超长单条显式截断且解码完整。静置窗口跨日也能回收。</summary>
    [TestMethod]
    public void RollsAndBoundsIndividualSegments()
    {
        using var store = new ActivityLogStore(_root.FullName);
        string line = new('中', ActivityLogStore.SegmentBytes / 5);
        store.Append(line, _now);
        store.Append(line, _now);
        Assert.HasCount(2, _root.GetFiles());
        foreach (var file in _root.GetFiles()) Assert.AreEqual(line + Environment.NewLine, File.ReadAllText(file.FullName));
        store.Append(new string('文', ActivityLogStore.SegmentBytes / 3 + 1), _now);
        var files = _root.GetFiles();
        Assert.HasCount(3, files);
        var largest = files.MaxBy(file => file.Length)!;
        Assert.IsTrue(largest.Length <= ActivityLogStore.SegmentBytes);
        Assert.EndsWith("（单条日志过长，已截断）" + Environment.NewLine, File.ReadAllText(largest.FullName, new UTF8Encoding(false, true)));
        store.Maintain(_now.AddDays(16));
        Assert.IsEmpty(_root.GetFiles());
    }

    /// <summary>启动即清过期安装和重启诊断，近期日志、自备附件和非缓存目录都保持原样。</summary>
    [TestMethod]
    public void UpdateMaintenanceUsesIndividualFileAge()
    {
        var owned = _root.CreateSubdirectory(new string('a', 64));
        var fresh = _root.CreateSubdirectory(new string('b', 64));
        var other = _root.CreateSubdirectory("备份");
        foreach (var dir in new[] { owned, fresh, other })
            foreach (string name in new[] { "install.log", "restart-error.log", "notes.txt" })
            {
                string file = Path.Combine(dir.FullName, name);
                File.WriteAllText(file, "测试");
                File.SetLastWriteTimeUtc(file, dir == fresh ? _now : _now.AddDays(-15));
            }
        new UpdateDownload(root: _root.FullName).RemoveExpiredLogs();
        CollectionAssert.AreEqual(new[] { "notes.txt" }, owned.GetFiles().Select(file => file.Name).ToArray());
        Assert.HasCount(3, fresh.GetFiles());
        Assert.HasCount(3, other.GetFiles());
    }

    /// <summary>淘汰包含多行的记录时，富文本选区只保留尚在屏幕上的字符及其严重程度颜色。</summary>
    [STATestMethod]
    public void ViewRetainsSelectionAcrossEviction()
    {
        using var view = new ActivityLogView();
        view.AppendEntry("首条\r\n第二行", Color.Teal);
        view.AppendEntry("要复制", Color.Maroon);
        for (int i = 2; i < ActivityLogStore.VisibleEntries; i++) view.AppendEntry($"记录{i}", Color.Teal);
        view.Select(0, view.Text.IndexOf("要复制", StringComparison.Ordinal) + 3);
        view.AppendEntry("新增", Color.Teal);
        Assert.StartsWith("要复制\n", view.Text);
        Assert.AreEqual("要复制", view.SelectedText);
        Assert.AreEqual(Color.Maroon.ToArgb(), view.SelectionColor.ToArgb());
        view.AppendEntry("再新增", Color.Teal);
        Assert.AreEqual(0, view.SelectionLength);
        Assert.StartsWith("记录2\n", view.Text);
    }
}
