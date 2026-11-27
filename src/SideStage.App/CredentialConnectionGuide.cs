/*
 * 文件职责：为没有保存登录凭据的用户选择首次输入、后续提醒或直接连接。
 * 持久化：两个界面共享当前 Windows 用户的引导进度。只有明确选择直接连接才能记住跳过提醒。
 */
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace SideStage.App;

internal enum CredentialChoice { Configure, Direct, Cancel }

internal sealed class CredentialConnectionGuide(string keyPath)
{
    // 这三个数值也是当前用户注册表里的稳定取值。
    private enum Stage { First = 0, Remind = 1, Skip = 2 }
    private const string ValueName = "CredentialConnectionGuide";
    private Stage _stage;

    /// <summary>每次从当前用户读取进度，让应用重启和多个窗口都能沿用同一次选择。</summary>
    internal CredentialChoice Choose(Func<(CredentialChoice Choice, bool Remember)> prompt, Action<string> warn)
    {
        var stage = ClaimAttempt(warn);
        if (stage == Stage.First) return CredentialChoice.Configure;
        if (stage == Stage.Skip) return CredentialChoice.Direct;

        var result = prompt();
        if (result is (CredentialChoice.Direct, true)) Save(Stage.Skip, warn);
        return result.Choice;
    }

    /// <summary>两个进程同时点击连接时，只有一个取得首次引导。读写完成即释放互斥量，再显示界面。</summary>
    private Stage ClaimAttempt(Action<string> warn)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var mutex = new Mutex(false, @"Global\SideStage.CredentialGuide." + identity.User!.Value);
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(1)))
                    return _stage == Stage.Skip ? Stage.Skip : Stage.Remind;
            }
            catch (AbandonedMutexException) { /* 前一个进程已退出，本线程已取得互斥量。 */ }
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                switch (key?.GetValue(ValueName))
                {
                    case (int)Stage.Skip: _stage = Stage.Skip; break;
                    case (int)Stage.Remind when _stage == Stage.First: _stage = Stage.Remind; break;
                }
                var current = _stage;
                if (current == Stage.First) Save(Stage.Remind, warn);
                return current;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            // 无法协调时仍允许用户选择配置或直接连接，不重复强制打开输入页。
            warn("暂时无法读取登录凭据引导进度。");
            return _stage == Stage.Skip ? Stage.Skip : Stage.Remind;
        }
    }

    /// <summary>保存引导进度，无法写入时本窗口仍可继续连接。</summary>
    private void Save(Stage stage, Action<string> warn)
    {
        _stage = stage;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(ValueName, (int)stage, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (IsStorageError(ex)) { warn("登录凭据引导的选择未能保存到下次启动。"); }
    }

    /// <summary>识别当前用户设置不可读写时可以降级处理的系统错误。</summary>
    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException;

}
