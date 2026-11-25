/*
 * 文件名称：ParentSessionRecoveryTests.cs
 * 文件职责：验证父桌面接回的归属、目标复核和输入复检。在内存中模拟控制台切换，不操作测试机的会话。
 */
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.Windows.Platform.Windows;
using Station = SideStage.Windows.Platform.Windows.ParentRecoverySessions.Station;

namespace SideStage.Core.Tests.Platform.Windows;

/// <summary>核对 Windows 父桌面恢复的选取条件、切换次数及复检结果。</summary>
[TestClass]
public sealed class ParentSessionRecoveryTests
{
    private static readonly Station[] Idle = [
        new(1, 4, "", "PC", "Ray"), new(2, 0, "Child", "PC", "Ray"), new(3, 1, "Console", "", "")];

    /// <summary>接回目的地必须是空闲物理控制台，父子必须同属当前账户。其他活动连接也会阻止切换。</summary>
    [TestMethod]
    public void SelectsOnlyOwnDisconnectedParentAndVacantConsole()
    {
        uint Select(Station[] rows, uint console = 3) => ParentRecoverySessions.SelectConsole(rows, @"PC\Ray", 1, 2, console);
        Assert.AreEqual(3u, Select(Idle));
        Assert.ThrowsExactly<InvalidOperationException>(() => Select(Idle, uint.MaxValue));
        Assert.ThrowsExactly<InvalidOperationException>(() => Select([Idle[0] with { Domain = "OTHER" }, Idle[1], Idle[2]]));
        Assert.ThrowsExactly<InvalidOperationException>(() => Select([Idle[0], Idle[1] with { User = "Other" }, Idle[2]]));
        Assert.ThrowsExactly<InvalidOperationException>(() => Select([Idle[0] with { State = 0 }, Idle[1], Idle[2]]));
        Assert.ThrowsExactly<InvalidOperationException>(() => Select([Idle[0], Idle[1], Idle[2] with { User = "Other" }]));
        Assert.ThrowsExactly<InvalidOperationException>(() => Select([.. Idle, new(4, 0, "RDP-Tcp#2", "PC", "Other")]));
    }

    /// <summary>覆盖输入正常、接回已确认、复检持续失败，以及接回请求被接受但物理控制台没有变化。</summary>
    [TestMethod]
    [DataRow(true, false, false, true, 0)]
    [DataRow(false, true, true, true, 1)]
    [DataRow(false, true, false, false, 1)]
    [DataRow(false, false, true, false, 1)]
    public async Task ReportsOnlyVerifiedInput(bool initial, bool restored, bool after, bool expected, int requests)
    {
        int connects = 0, probes = 0;
        var result = await WindowsParentSessionRecovery.RunAsync(() => 3, _ => connects++, () => restored,
            _ => Task.FromResult(probes++ == 0 ? initial : after), (_, _) => Task.CompletedTask, default);
        Assert.AreEqual(requests, connects);
        Assert.AreEqual(expected, result.InputReady);
        Assert.AreEqual(initial || !restored ? 1 : after ? 2 : 6, probes);
    }

    /// <summary>探测返回前用户取消、代理握手失败或控制台编号变化，都不能发出桌面切换请求。</summary>
    [TestMethod]
    public async Task RechecksImmediatelyBeforeTransfer()
    {
        int reads = 0;
        static void Unexpected(uint _) => Assert.Fail("不应执行接回。");
        static Task NoWait(TimeSpan _, CancellationToken __) => Task.CompletedTask;
        var changed = await WindowsParentSessionRecovery.RunAsync(() => (uint)++reads, Unexpected,
            () => false, _ => Task.FromResult(false), NoWait, default);
        Assert.IsFalse(changed.InputReady);
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsParentSessionRecovery.RunAsync(() => 3, Unexpected,
            () => false, _ => { stop.Cancel(); return Task.FromResult(false); }, NoWait, stop.Token));
        await Assert.ThrowsExactlyAsync<IOException>(() => WindowsParentSessionRecovery.RunAsync(() => 3, Unexpected,
            () => false, _ => throw new IOException(), NoWait, default));
    }
}
