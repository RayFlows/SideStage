/*
 * 文件名称：WindowsChildSessionCredentials.cs
 * 文件职责：
 *   在当前进程中短暂承载 Windows 子会话登录所需的账户名、域和受保护密码。
 *
 * 安全边界：
 *   - 密码只以 SecureString 形式保存在此对象中，不提供明文读取属性。
 *   - 调用方负责在连接完成后释放对象；释放会清理 SecureString。
 *   - 本类型不负责持久化；持久化由 WindowsChildSessionCredentialStore 负责。
 *   - 把凭据交给 RDP ActiveX 时，Windows COM 互操作仍会短暂创建其所需的明文副本。
 */

using System.Security;

namespace SideStage.Windows.RemoteDesktop;

/// <summary>
/// 表示一次 Windows 子会话连接所需的登录凭据。
/// </summary>
public sealed class WindowsChildSessionCredentials : IDisposable
{
    private readonly SecureString _password;
    private bool _disposed;

    /// <summary>
    /// 获取 Windows 登录账户名，不含域前缀。
    /// </summary>
    public string UserName { get; }

    /// <summary>
    /// 获取 Windows 登录域或计算机名；留空表示交由 Windows 使用默认范围。
    /// </summary>
    public string Domain { get; }

    /// <summary>
    /// 建立一次性登录凭据对象，并复制调用方提供的受保护密码。
    /// </summary>
    /// <param name="userName">Windows 账户名。</param>
    /// <param name="domain">可选的域或计算机名。</param>
    /// <param name="password">受保护的 Windows 账户密码。</param>
    /// <exception cref="ArgumentException">账户名或密码为空时抛出。</exception>
    /// <exception cref="ArgumentNullException">参数为 null 时抛出。</exception>
    public WindowsChildSessionCredentials(string userName, string? domain, SecureString password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0)
        {
            throw new ArgumentException("Windows 账户密码不能为空。", nameof(password));
        }

        UserName = userName.Trim();
        Domain = domain?.Trim() ?? string.Empty;
        _password = password.Copy();
        _password.MakeReadOnly();
    }

    /// <summary>
    /// 释放对象持有的密码缓冲区。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _password.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// 创建供本程序集内部组件使用的密码副本。
    /// </summary>
    /// <returns>需要由调用方释放的受保护密码副本。</returns>
    /// <exception cref="ObjectDisposedException">凭据对象已释放时抛出。</exception>
    internal SecureString CopyPassword()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _password.Copy();
    }
}
