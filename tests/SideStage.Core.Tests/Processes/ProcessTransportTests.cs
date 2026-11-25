/*
 * 文件职责：通过真实的本机任务计划程序和已部署代理核对握手、PID 与进程退出。
 * 测试刻意限定到本会话中的短命控制台程序，只验证传输层。生产入口的子会话
 * 身份校验另有独立测试，不能将本项结果记为子桌面隔离通过。
 */
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.Core.Processes;
using SideStage.Core.Sessions;
using SideStage.Windows.Processes;

namespace SideStage.Core.Tests.Processes;

/// <summary>验证一次性代理在自己的会话中可以准确交付 Windows 进程句柄。</summary>
[TestClass]
public sealed class ProcessTransportTests
{
    /// <summary>
    /// 显式开启本机验收时，让普通权限的一次性代理通过专用管道返回实际输入测试结果。
    /// 在当前已解锁桌面只发送一次零位移事件，不切换会话或打开应用。
    /// </summary>
    [TestMethod]
    public async Task Transport_ProbesInteractiveInput()
    {
        if (Environment.GetEnvironmentVariable("SIDESTAGE_TEST_TRANSPORT") != "1")
            Assert.Inconclusive("需要显式启用本机临时计划任务验收。");
        var current = new SideStage.Core.Platform.Windows.WindowsSessionInspector().GetCurrentSession();
        if (!current.IsDesktopReady) Assert.Inconclusive("输入探针验收需要已解锁的交互桌面。");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        bool received = await new WindowsTaskSchedulerChildSessionProcessStarter().ProbeInputAsync(current.Id, timeout.Token);
        Assert.IsTrue(received, "普通权限代理应能在当前交互桌面投递零位移输入。");
    }

    /// <summary>按需运行有副作用的传输验收，仅操作由此测试创建的有限时长回环 ping。</summary>
    /// <param name="terminate">主动终止或等待自然退出，两条路径都应保留准确的最终状态。</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Transport_TracksOwnedProcess(bool terminate)
    {
        if (Environment.GetEnvironmentVariable("SIDESTAGE_TEST_TRANSPORT") != "1")
        {
            Assert.Inconclusive("需要显式启用本机临时计划任务验收。");
        }

        using Process current = Process.GetCurrentProcess();
        SessionId session = new((uint)current.SessionId);
        string file = Path.Combine(Environment.SystemDirectory, "ping.exe");
        ChildProcessStartRequest request = new(file, ["-n", "4", "127.0.0.1"]);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));
        Process child = await new WindowsTaskSchedulerChildSessionProcessStarter().StartAsync(session, request, timeout.Token);
        using WindowsManagedChildProcess handle = new(child, session, file);
        Assert.AreEqual(ChildProcessState.Running, handle.GetSnapshot().State);
        ChildProcessSnapshot final = terminate ? await handle.TerminateAsync(timeout.Token) : await handle.WaitForExitAsync(timeout.Token);
        Assert.AreEqual(terminate ? ChildProcessState.Terminated : ChildProcessState.Exited, final.State);
        Assert.IsNotNull(final.ExitCode);
    }
}
