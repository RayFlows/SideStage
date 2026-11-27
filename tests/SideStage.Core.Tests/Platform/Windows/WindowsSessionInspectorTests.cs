/*
 * 文件名称：WindowsSessionInspectorTests.cs
 * 文件职责：
 *   对 Windows 会话观察器进行只读集成验证。
 *
 * 测试边界：
 *   - 仅在 Windows x64 主机运行。
 *   - 读取调用进程会话、子会话开关以及当前子会话编号。
 *   - 不创建、连接、断开或结束会话，不修改系统配置。
 */

using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.Core.Platform.Windows;
using SideStage.Core.Sessions;

namespace SideStage.Core.Tests.Platform.Windows;

/// <summary>
/// 验证 Windows 原生会话查询 API 与公开模型之间的映射。
/// </summary>
[TestClass]
public sealed class WindowsSessionInspectorTests
{
    /// <summary>
    /// 验证当前会话快照包含有效编号、已知枚举值和 UTC 采集时间。
    /// </summary>
    [TestMethod]
    public void GetCurrentSession_ReturnsWellFormedSnapshot()
    {
        WindowsSessionInspector inspector = new();

        SessionSnapshot snapshot = inspector.GetCurrentSession();

        Assert.IsTrue(Enum.IsDefined(snapshot.ConnectionState));
        Assert.IsFalse(snapshot.IsChildSession);
        Assert.AreEqual(TimeSpan.Zero, snapshot.ObservedAtUtc.Offset);
    }

    /// <summary>
    /// 验证子会话查询只返回“无子会话”或带有效编号的快照。
    /// </summary>
    [TestMethod]
    public void TryGetChildSession_ReturnsNullOrValidSnapshot()
    {
        WindowsSessionInspector inspector = new();

        SessionSnapshot? snapshot = inspector.TryGetChildSession();

        if (snapshot is not null)
        {
            Assert.IsTrue(snapshot.IsChildSession);
            Assert.IsTrue(Enum.IsDefined(snapshot.ConnectionState));
        }
    }

    /// <summary>
    /// 重放本机出现的情况：Windows 已返回子会话编号，扩展查询却返回 1733。
    /// 宿主仍应能读取编号与诊断码，且不能据此认定子会话已经解锁。
    /// </summary>
    [TestMethod]
    [DataRow(2, false)]
    [DataRow(5, true)]
    [DataRow(7022, false)]
    public void ChildInfoEx_InvalidTagPreservesIdAndErrorWithoutGrantingDesktopAccess(int basicError, bool canShowSignIn)
    {
        SessionId child = new(2);
        SessionSnapshot snapshot = WindowsSessionInspector.HandleSnapshotQueryError(child, true, 1733, id =>
        {
            Assert.AreEqual(child, id);
            return (SessionConnectionState.Unknown, basicError);
        });

        Assert.AreEqual(child, snapshot.Id);
        Assert.IsTrue(snapshot.IsChildSession);
        Assert.AreEqual(1733, snapshot.StateQueryError);
        Assert.AreEqual(basicError, snapshot.BasicStateQueryError);
        Assert.AreEqual(canShowSignIn, snapshot.CanPresentSignIn);
        Assert.AreEqual(SessionConnectionState.Unknown, snapshot.ConnectionState);
        Assert.IsNull(snapshot.IsLocked);
        Assert.IsFalse(snapshot.IsDesktopReady);
    }

    /// <summary>
    /// 父会话的查询错误及未识别的子会话错误必须继续报告给调用方。
    /// </summary>
    [TestMethod]
    [DataRow(false, 1733)]
    [DataRow(true, 1722)]
    public void UnhandledQueryError_PreservesWin32Exception(bool child, int error)
    {
        Win32Exception exception = Assert.ThrowsExactly<Win32Exception>(
            () => WindowsSessionInspector.HandleSnapshotQueryError(new SessionId(1), child, error,
                _ => throw new AssertFailedException("此错误不应回退到基础连接状态查询。")));
        Assert.AreEqual(error, exception.NativeErrorCode);
    }

    /// <summary>
    /// 验证子会话能力 API 能够完成原生查询且不抛出平台错误。
    /// </summary>
    [TestMethod]
    public void IsChildSessionsEnabled_CompletesWithoutNativeError()
    {
        WindowsSessionInspector inspector = new();

        _ = inspector.IsChildSessionsEnabled();
    }
}
