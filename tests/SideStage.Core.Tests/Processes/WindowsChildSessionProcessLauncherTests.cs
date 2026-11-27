/*
 * 文件名称：WindowsChildSessionProcessLauncherTests.cs
 * 文件职责：
 *   验证 Windows 跨会话启动器在进入 Task Scheduler 前会严格拒绝不可信目标。
 *
 * 测试边界：
 *   - 使用假的只读会话观察器，不改变本机 Windows 会话状态。
 *   - 所有失败断言都发生在启动后端之前，因此不会创建任务或进程。
 *   - 真实子会话内启动、进程发现和终止由人工集成验收覆盖。
 */

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.Core.Processes;
using SideStage.Core.Sessions;
using SideStage.Windows.Processes;

namespace SideStage.Core.Tests.Processes;

/// <summary>
/// 验证跨会话启动的前置身份校验和不回退约束。
/// </summary>
[TestClass]
public sealed class WindowsChildSessionProcessLauncherTests
{
    /// <summary>
    /// 覆盖 RDP 重连后连接状态已活动、子会话仍在解锁界面的情况。
    /// 不能确认解锁标志时也必须在投递代理之前拒绝请求。
    /// </summary>
    /// <param name="locked">系统报告已锁定，或无法读取明确标志。</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(null)]
    public void ValidateTarget_RefusesLockedOrUnknownDesktop(bool? locked)
    {
        SessionSnapshot parent = CreateSnapshot(new SessionId(1), SessionConnectionState.Active, false);
        SessionSnapshot child = CreateSnapshot(new SessionId(3), SessionConnectionState.Active, true) with { IsLocked = locked };
        FakeSessionInspector inspector = new(parent, child);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => WindowsChildSessionProcessLauncher.ValidateTargetChildSession(inspector, child.Id));
    }

    /// <summary>
    /// 验证没有子会话时启动请求会在 Task Scheduler 之前失败。
    /// </summary>
    [TestMethod]
    public async Task StartAsync_RejectsWhenNoChildSessionExists()
    {
        SessionSnapshot parent = CreateSnapshot(
            new SessionId(7),
            SessionConnectionState.Active,
            isChildSession: false);
        FakeSessionInspector inspector = new(parent, child: null);
        WindowsChildSessionProcessLauncher launcher = new(inspector);
        ChildProcessStartRequest request = CreateRequest();

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => launcher.StartAsync(new SessionId(8), request));

        StringAssert.Contains(exception.Message, "没有已建立的子会话");
    }

    /// <summary>
    /// 验证传入父会话编号时不会把程序启动回主桌面。
    /// </summary>
    [TestMethod]
    public async Task StartAsync_RejectsParentSessionId()
    {
        SessionSnapshot parent = CreateSnapshot(
            new SessionId(7),
            SessionConnectionState.Active,
            isChildSession: false);
        SessionSnapshot child = CreateSnapshot(
            new SessionId(8),
            SessionConnectionState.Active,
            isChildSession: true);
        FakeSessionInspector inspector = new(parent, child);
        WindowsChildSessionProcessLauncher launcher = new(inspector);
        ChildProcessStartRequest request = CreateRequest();

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => launcher.StartAsync(new SessionId(7), request));

        StringAssert.Contains(exception.Message, "不是当前活动子会话");
    }

    /// <summary>
    /// 验证断开或未激活的子会话不会被当成可启动的 GUI 桌面。
    /// </summary>
    [TestMethod]
    public async Task StartAsync_RejectsInactiveChildSession()
    {
        SessionSnapshot parent = CreateSnapshot(
            new SessionId(7),
            SessionConnectionState.Active,
            isChildSession: false);
        SessionSnapshot child = CreateSnapshot(
            new SessionId(8),
            SessionConnectionState.Disconnected,
            isChildSession: true);
        FakeSessionInspector inspector = new(parent, child);
        WindowsChildSessionProcessLauncher launcher = new(inspector);
        ChildProcessStartRequest request = CreateRequest();

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => launcher.StartAsync(new SessionId(8), request));

        StringAssert.Contains(exception.Message, "不是活动状态");
    }

    /// <summary>
    /// 创建一个测试用的合法进程请求。
    /// </summary>
    /// <returns>当前测试运行时的可执行文件请求。</returns>
    private static ChildProcessStartRequest CreateRequest()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new AssertFailedException("测试进程没有可用的 Environment.ProcessPath。");
        return new ChildProcessStartRequest(executablePath);
    }

    /// <summary>
    /// 创建指定编号和状态的会话快照。
    /// </summary>
    /// <param name="sessionId">会话编号。</param>
    /// <param name="state">连接状态。</param>
    /// <param name="isChildSession">是否来自子会话查询路径。</param>
    /// <returns>测试用会话快照。</returns>
    private static SessionSnapshot CreateSnapshot(
        SessionId sessionId,
        SessionConnectionState state,
        bool isChildSession)
    {
        return new SessionSnapshot(
            sessionId,
            state,
            isChildSession,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 提供不会触碰 Windows 原生 API 的只读会话观察器测试替身。
    /// </summary>
    private sealed class FakeSessionInspector : ISessionInspector
    {
        private readonly SessionSnapshot _parent;
        private readonly SessionSnapshot? _child;

        /// <summary>
        /// 创建测试会话观察器。
        /// </summary>
        /// <param name="parent">父会话快照。</param>
        /// <param name="child">可选子会话快照。</param>
        public FakeSessionInspector(SessionSnapshot parent, SessionSnapshot? child)
        {
            _parent = parent;
            _child = child;
        }

        /// <summary>
        /// 返回固定的父会话快照。
        /// </summary>
        /// <returns>父会话快照。</returns>
        public SessionSnapshot GetCurrentSession() => _parent;

        /// <summary>
        /// 返回固定的子会话快照。
        /// </summary>
        /// <returns>子会话快照或 null。</returns>
        public SessionSnapshot? TryGetChildSession() => _child;

        /// <summary>
        /// 返回测试环境中固定的能力开关状态。
        /// </summary>
        /// <returns>始终返回 true，表示能力检查不是本测试目标。</returns>
        public bool IsChildSessionsEnabled() => true;
    }
}
