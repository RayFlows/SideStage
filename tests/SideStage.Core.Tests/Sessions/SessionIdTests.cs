/*
 * 文件名称：SessionIdTests.cs
 * 文件职责：
 *   验证会话编号值对象对 Windows 无效会话哨兵值的约束。
 *
 * 测试范围：
 *   - 验证普通有效编号和边界编号可以构造。
 *   - 验证 0xFFFFFFFF 哨兵值会被拒绝。
 *   - 不调用 Windows API，不改变系统状态。
 */

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.Core.Sessions;

namespace SideStage.Core.Tests.Sessions;

/// <summary>
/// 覆盖 <see cref="SessionId"/> 的值约束。
/// </summary>
[TestClass]
public sealed class SessionIdTests
{
    /// <summary>
    /// 验证有效会话编号能够原样保存在值对象中。
    /// </summary>
    [TestMethod]
    public void Constructor_WithValidValue_PreservesValue()
    {
        SessionId sessionId = new(42);

        Assert.AreEqual(42u, sessionId.Value);
        Assert.AreEqual("Session(42)", sessionId.ToString());
    }

    /// <summary>
    /// 验证 Windows 无效会话哨兵值不能被表示为有效编号。
    /// </summary>
    [TestMethod]
    public void Constructor_WithNoSessionSentinel_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionId(uint.MaxValue));
    }
}
