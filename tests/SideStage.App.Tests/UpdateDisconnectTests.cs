/*
 * 文件职责：验证更新只请求断开预览，必须等到原连接确实断开才交还控制。
 * 测试边界：使用内存状态代替 ActiveX，覆盖断开被拒绝、取消、等待和已有音频重连请求。
 */
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.App;

namespace SideStage.App.Tests;

[TestClass]
public sealed class UpdateDisconnectTests
{
    /// <summary>活动连接恰好收到一次断开；音频切换已发出断开时只等待，不重复发送。</summary>
    [TestMethod]
    [DataRow(true, false, 1)]
    [DataRow(true, true, 0)]
    [DataRow(false, false, 0)]
    public async Task WaitsForConfirmedDisconnect(bool active, bool audioPending, int expected)
    {
        int calls = 0;
        var end = ParchmentPreviewForm.DisconnectForUpdateAsync(() => audioPending, () => Volatile.Read(ref active), () => calls++, default);
        Assert.AreEqual(expected, calls);
        if (active) Assert.IsFalse(end.IsCompleted);
        Volatile.Write(ref active, false);
        await end.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>断开被拒绝或超时的分支必须向界面返回失败，不能当作允许退出安装。</summary>
    [TestMethod]
    public async Task RejectionOrCancellationPreventsHandoff()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            ParchmentPreviewForm.DisconnectForUpdateAsync(() => false, () => true, () => throw new InvalidOperationException(), default));
        using var stop = new CancellationTokenSource();
        var pending = ParchmentPreviewForm.DisconnectForUpdateAsync(() => true, () => true,
            () => Assert.Fail("不应重复发送断开。"), stop.Token);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }
}
