/*
 * 文件名称：WindowsChildSessionCredentialStore.cs
 * 文件职责：
 *   使用 Windows Credential Manager 为当前 Windows 用户保存子会话登录凭据。
 *
 * 安全与生命周期约束：
 *   - 只写入 Windows 通用凭据库，不创建普通文本或项目配置文件。
 *   - 凭据由 Windows 按当前用户保护，并以本机持久方式保存。
 *   - 读取结果只返回受保护密码对象；调用方必须及时 Dispose。
 *   - 密码缓冲区在写入完成后清零；读取的原生凭据由 CredFree 释放。
 *   - 不记录用户名、域或密码，也不包含任何网络上传逻辑。
 *
 * 原生互操作说明：
 *   本文件只封装 advapi32.dll 的 CredRead、CredWrite、CredDelete 和 CredFree。
 *   凭据条目使用固定应用目标名，避免影响系统或其他应用保存的条目。
 */

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.Windows.Platform.Windows;

/// <summary>
/// 将子会话登录凭据保存到当前 Windows 用户的凭据管理器。
/// </summary>
public sealed class WindowsChildSessionCredentialStore
{
    // 这是凭据管理器中的持久键，不是展示名。保持 beta.1 的键即可原地复用，无需搬运密码。
    private const string DefaultTargetName = "WinChildDesk.ChildSession.AutoLogon.v1";
    private const uint GenericCredentialType = 1;
    private const uint PersistForLocalMachine = 2;
    private const int CredentialNotFound = 1168;
    private const int MaximumCredentialBlobSize = 2560;
    private readonly string _targetName;

    /// <summary>
    /// 建立使用 SideStage 固定目标名的凭据库访问器。
    /// </summary>
    public WindowsChildSessionCredentialStore()
        : this(DefaultTargetName)
    {
    }

    /// <summary>
    /// 建立凭据库访问器；内部目标名构造函数用于隔离集成测试条目。
    /// </summary>
    /// <param name="targetName">Windows 凭据管理器中的应用专属目标名。</param>
    /// <exception cref="ArgumentException">目标名为空时抛出。</exception>
    internal WindowsChildSessionCredentialStore(string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        _targetName = targetName;
    }

    /// <summary>
    /// 读取当前用户保存的子会话登录凭据。
    /// </summary>
    /// <returns>找到凭据时返回需释放的对象；不存在时返回 null。</returns>
    /// <exception cref="Win32Exception">Windows 凭据 API 调用失败时抛出。</exception>
    /// <exception cref="InvalidDataException">系统返回的凭据格式不符合预期时抛出。</exception>
    public WindowsChildSessionCredentials? Read()
    {
        if (!TryReadEntry(out IntPtr credentialPointer))
        {
            return null;
        }

        NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
        try
        {
            string storedAccount = Marshal.PtrToStringUni(credential.UserName)
                ?? throw new InvalidDataException("Windows 凭据条目没有账户名。");
            (string domain, string userName) = SplitStoredAccount(storedAccount);
            using SecureString password = ReadPassword(credential);
            return new WindowsChildSessionCredentials(userName, domain, password);
        }
        finally
        {
            ClearAndFreeEntry(credentialPointer, credential);
        }
    }

    /// <summary>
    /// 检查应用的保存项是否存在。只使用原生结构头，不解析账户或复制密码，不验证登录。
    /// </summary>
    /// <returns>凭据存在时返回 true。</returns>
    public bool HasSavedCredentials()
    {
        if (!TryReadEntry(out IntPtr pointer))
        {
            return false;
        }

        ClearAndFreeEntry(pointer, Marshal.PtrToStructure<NativeCredential>(pointer));
        return true;
    }

    /// <summary>
    /// 按本库专属目标名查找一条通用凭据。仅将条目不存在视为正常缺省，其他系统错误交由调用方处理。
    /// </summary>
    private bool TryReadEntry(out IntPtr pointer)
    {
        if (CredRead(_targetName, GenericCredentialType, flags: 0, out pointer)) return true;
        int errorCode = Marshal.GetLastPInvokeError();
        if (errorCode == CredentialNotFound) return false;
        throw new Win32Exception(errorCode, "读取 Windows 子会话凭据失败。");
    }

    /// <summary>
    /// 擦除系统调用返回的临时密码数据，然后归还整块原生内存。存在性查询也走同一条清理路径。
    /// </summary>
    private static void ClearAndFreeEntry(IntPtr pointer, NativeCredential credential)
    {
        try
        {
            if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize <= MaximumCredentialBlobSize)
            {
                for (int index = 0; index < credential.CredentialBlobSize; index++)
                    Marshal.WriteByte(credential.CredentialBlob, index, 0);
            }
        }
        finally { CredFree(pointer); }
    }

    /// <summary>
    /// 将凭据写入当前 Windows 用户的本机持久通用凭据条目。
    /// </summary>
    /// <param name="credentials">需要保存的账户名、域和密码。</param>
    /// <exception cref="ArgumentNullException">凭据对象为 null 时抛出。</exception>
    /// <exception cref="ArgumentException">密码超出 Windows 凭据库容量时抛出。</exception>
    /// <exception cref="Win32Exception">Windows 凭据 API 调用失败时抛出。</exception>
    public void Save(WindowsChildSessionCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        using SecureString password = credentials.CopyPassword();
        int blobSize = checked(password.Length * sizeof(char));
        if (blobSize > MaximumCredentialBlobSize)
        {
            throw new ArgumentException("账户密码超过 Windows 凭据库的容量上限。", nameof(credentials));
        }

        IntPtr passwordPointer = Marshal.SecureStringToBSTR(password);
        IntPtr targetPointer = IntPtr.Zero;
        IntPtr commentPointer = IntPtr.Zero;
        IntPtr userNamePointer = IntPtr.Zero;
        try
        {
            targetPointer = Marshal.StringToHGlobalUni(_targetName);
            commentPointer = Marshal.StringToHGlobalUni("SideStage 子会话登录凭据");
            string accountName = string.IsNullOrWhiteSpace(credentials.Domain)
                ? credentials.UserName
                : $"{credentials.Domain}\\{credentials.UserName}";
            userNamePointer = Marshal.StringToHGlobalUni(accountName);
            NativeCredential credential = new()
            {
                Type = GenericCredentialType,
                TargetName = targetPointer,
                Comment = commentPointer,
                CredentialBlobSize = checked((uint)blobSize),
                CredentialBlob = passwordPointer,
                Persist = PersistForLocalMachine,
                UserName = userNamePointer,
            };

            if (!CredWrite(ref credential, flags: 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "保存 Windows 子会话凭据失败。");
            }
        }
        finally
        {
            Marshal.ZeroFreeBSTR(passwordPointer);
            FreeHGlobal(userNamePointer);
            FreeHGlobal(commentPointer);
            FreeHGlobal(targetPointer);
        }
    }

    /// <summary>
    /// 删除当前用户保存的子会话登录凭据；不存在时视为已完成。
    /// </summary>
    /// <exception cref="Win32Exception">Windows 凭据 API 调用失败时抛出。</exception>
    public void Delete()
    {
        if (CredDelete(_targetName, GenericCredentialType, flags: 0))
        {
            return;
        }

        int errorCode = Marshal.GetLastPInvokeError();
        if (errorCode != CredentialNotFound)
        {
            throw new Win32Exception(errorCode, "删除 Windows 子会话凭据失败。");
        }
    }

    /// <summary>
    /// 从凭据的 UTF-16 原生数据块构建受保护密码，并验证长度与指针。
    /// </summary>
    /// <param name="credential">由 Windows 返回的原生凭据结构。</param>
    /// <returns>调用方负责释放的受保护密码。</returns>
    /// <exception cref="InvalidDataException">数据块长度或指针非法时抛出。</exception>
    private static SecureString ReadPassword(NativeCredential credential)
    {
        if (credential.CredentialBlob == IntPtr.Zero
            || credential.CredentialBlobSize == 0
            || credential.CredentialBlobSize > MaximumCredentialBlobSize
            || credential.CredentialBlobSize % sizeof(char) != 0)
        {
            throw new InvalidDataException("Windows 子会话凭据中的密码数据格式无效。");
        }

        int characterCount = checked((int)credential.CredentialBlobSize / sizeof(char));
        SecureString password = new();
        for (int index = 0; index < characterCount; index++)
        {
            password.AppendChar((char)Marshal.ReadInt16(credential.CredentialBlob, index * sizeof(char)));
        }

        password.MakeReadOnly();
        return password;
    }

    /// <summary>
    /// 将凭据库中保存的“域\账户”字符串拆分成 ActiveX 所需字段。
    /// </summary>
    /// <param name="storedAccount">Windows 凭据结构中的用户名字段。</param>
    /// <returns>域与账户名；未包含反斜杠时域为空。</returns>
    private static (string Domain, string UserName) SplitStoredAccount(string storedAccount)
    {
        int separatorIndex = storedAccount.IndexOf('\\');
        return separatorIndex < 0
            ? (string.Empty, storedAccount)
            : (storedAccount[..separatorIndex], storedAccount[(separatorIndex + 1)..]);
    }

    /// <summary>
    /// 释放非托管字符串缓冲区；允许指针尚未分配。
    /// </summary>
    /// <param name="pointer">待释放的非托管指针。</param>
    private static void FreeHGlobal(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>
    /// Windows CREDENTIAL 结构在当前进程中的布局。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        /// <summary>凭据标记。</summary>
        public uint Flags;

        /// <summary>凭据类型。</summary>
        public uint Type;

        /// <summary>应用目标名指针。</summary>
        public IntPtr TargetName;

        /// <summary>可选描述指针。</summary>
        public IntPtr Comment;

        /// <summary>最近写入时间。</summary>
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;

        /// <summary>凭据数据块长度。</summary>
        public uint CredentialBlobSize;

        /// <summary>凭据数据块指针。</summary>
        public IntPtr CredentialBlob;

        /// <summary>凭据持久级别。</summary>
        public uint Persist;

        /// <summary>扩展属性数量。</summary>
        public uint AttributeCount;

        /// <summary>扩展属性数组指针。</summary>
        public IntPtr Attributes;

        /// <summary>可选目标别名指针。</summary>
        public IntPtr TargetAlias;

        /// <summary>账户名指针。</summary>
        public IntPtr UserName;
    }

    /// <summary>
    /// 将通用凭据写入当前用户的 Windows 凭据库。
    /// </summary>
    /// <param name="credential">需要写入的 Windows 凭据。</param>
    /// <param name="flags">保留标志，必须为零。</param>
    /// <returns>写入成功时为 true。</returns>
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    /// <summary>
    /// 读取当前用户凭据库中的指定通用凭据。
    /// </summary>
    /// <param name="targetName">凭据目标名。</param>
    /// <param name="type">凭据类型。</param>
    /// <param name="flags">保留标志，必须为零。</param>
    /// <param name="credential">成功时由系统分配的凭据结构指针。</param>
    /// <returns>找到并成功读取凭据时为 true。</returns>
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    /// <summary>
    /// 删除当前用户凭据库中的指定通用凭据。
    /// </summary>
    /// <param name="targetName">凭据目标名。</param>
    /// <param name="type">凭据类型。</param>
    /// <param name="flags">保留标志，必须为零。</param>
    /// <returns>成功删除时为 true。</returns>
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    /// <summary>
    /// 释放 CredRead 分配的 Windows 凭据结构及其子缓冲区。
    /// </summary>
    /// <param name="credential">由 CredRead 返回的结构指针。</param>
    [DllImport("advapi32.dll", EntryPoint = "CredFree", ExactSpelling = true)]
    private static extern void CredFree(IntPtr credential);
}
