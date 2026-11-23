/*
 * 文件名称：WindowsChildSessionCredentialStoreTests.cs
 * 文件职责：
 *   验证 SideStage 凭据库对当前 Windows 用户凭据管理器的保存、读取和删除流程。
 *
 * 测试安全边界：
 *   - 每次测试使用随机目标名和固定的虚构密码，不触碰正式凭据条目。
 *   - 测试退出时无论成功失败都会删除其临时通用凭据。
 *   - 不建立 RDP 连接，不创建或注销 Windows 子会话。
 */

using System.Runtime.InteropServices;
using System.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.Windows.Platform.Windows;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.Core.Tests.Platform.Windows;

/// <summary>
/// 覆盖本机 Windows 凭据管理器的最小往返路径。
/// </summary>
[TestClass]
public sealed class WindowsChildSessionCredentialStoreTests
{
    /// <summary>
    /// 验证账户信息和密码可通过 Windows 凭据管理器保存、重读并删除。
    /// </summary>
    [TestMethod]
    public void SaveReadAndDelete_RoundTripsCredential()
    {
        string targetName = $"SideStage.Tests.{Guid.NewGuid():N}";
        WindowsChildSessionCredentialStore store = new(targetName);
        using SecureString inputPassword = CreateSecureString("Test-Only-Password-42!");
        using WindowsChildSessionCredentials input = new("TestUser", "TestDomain", inputPassword);

        try
        {
            store.Save(input);

            using WindowsChildSessionCredentials? actual = store.Read();
            Assert.IsNotNull(actual);
            Assert.AreEqual("TestUser", actual.UserName);
            Assert.AreEqual("TestDomain", actual.Domain);
            Assert.AreEqual("Test-Only-Password-42!", RevealTestPassword(actual));

            store.Delete();
            Assert.IsNull(store.Read());
        }
        finally
        {
            store.Delete();
        }
    }

    /// <summary>
    /// 为测试凭据构造 SecureString。
    /// </summary>
    /// <param name="value">虚构的测试密码。</param>
    /// <returns>只读的受保护字符串。</returns>
    private static SecureString CreateSecureString(string value)
    {
        SecureString result = new();
        foreach (char character in value)
        {
            result.AppendChar(character);
        }

        result.MakeReadOnly();
        return result;
    }

    /// <summary>
    /// 在测试断言边界读取密码，并立即释放非托管临时缓冲区。
    /// </summary>
    /// <param name="credentials">刚从临时测试目标名读取的凭据。</param>
    /// <returns>仅供固定测试数据比较的临时字符串。</returns>
    private static string RevealTestPassword(WindowsChildSessionCredentials credentials)
    {
        using SecureString password = credentials.CopyPassword();
        IntPtr pointer = Marshal.SecureStringToBSTR(password);
        try
        {
            return Marshal.PtrToStringBSTR(pointer);
        }
        finally
        {
            Marshal.ZeroFreeBSTR(pointer);
        }
    }
}
