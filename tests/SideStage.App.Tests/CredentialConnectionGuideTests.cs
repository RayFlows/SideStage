/*
 * 文件职责：验证当前 Windows 用户的首次凭据引导、后续提醒和主动取消提醒的持久化。
 * 隔离边界：每个用例使用随机注册表项，重新创建引导对象模拟重启，不读取用户密码或建立连接。
 */
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace SideStage.App.Tests;

[TestClass]
public sealed class CredentialConnectionGuideTests
{
    /// <summary>重开后只有明确勾选并选择直接连接才跳过以后提醒，取消、配置、单次直接连接都保留提醒。</summary>
    [TestMethod]
    public void ReopeningRemindsUntilDirectConnectionIsRemembered()
    {
        string key = $@"Software\SideStage.Tests\{Guid.NewGuid():N}";
        try
        {
            var answers = new Queue<(CredentialChoice Choice, bool Remember)>();
            CredentialChoice Reopen() => new CredentialConnectionGuide(key).Choose(answers.Dequeue, message => Assert.Fail(message));
            Assert.AreEqual(CredentialChoice.Configure, Reopen());
            foreach (var choice in new[] { (CredentialChoice.Cancel, true), (CredentialChoice.Configure, true),
                         (CredentialChoice.Direct, false), (CredentialChoice.Direct, true) })
            {
                answers.Enqueue(choice);
                Assert.AreEqual(choice.Item1, Reopen());
                Assert.IsEmpty(answers);
            }
            Assert.AreEqual(CredentialChoice.Direct, Reopen());
        }
        finally { Registry.CurrentUser.DeleteSubKey(key, false); }
    }

    /// <summary>两个窗口同时首次连接时，只有一个打开输入页，另一个进入提醒流程。</summary>
    [TestMethod]
    public void ConcurrentFirstAttemptsClaimEditorOnce()
    {
        string key = $@"Software\SideStage.Tests\{Guid.NewGuid():N}";
        try
        {
            var results = new CredentialChoice[2];
            Parallel.For(0, results.Length, i => results[i] = new CredentialConnectionGuide(key).Choose(
                () => (CredentialChoice.Cancel, false), message => Assert.Fail(message)));
            CollectionAssert.AreEquivalent(new[] { CredentialChoice.Configure, CredentialChoice.Cancel }, results);
        }
        finally { Registry.CurrentUser.DeleteSubKey(key, false); }
    }
}
