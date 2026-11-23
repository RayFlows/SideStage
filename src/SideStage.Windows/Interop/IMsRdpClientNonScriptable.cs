/*
 * 文件名称：IMsRdpClientNonScriptable.cs
 * 文件职责：
 *   声明 RDP ActiveX 的非脚本化凭据接口，用于提交和清除登录密码。
 *
 * 原生互操作约束：
 *   - 接口 IID 与 mstscax.dll 类型库中的 IMsRdpClientNonScriptable 对应。
 *   - COM 方法必须严格保持系统类型库的 vtable 顺序。
 *   - ResetPassword 位于 ClearTextPassword 后续的原生接口槽位，不能随意删改前序声明。
 *   - ClearTextPassword 仅在连接流程需要时设置；处理登录结果后立即调用 ResetPassword。
 */

using System.Runtime.InteropServices;

namespace SideStage.Windows.Interop;

/// <summary>
/// 用于向微软 RDP ActiveX 设置或清除非脚本化登录凭据的 COM 接口。
/// </summary>
[ComImport]
[Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpClientNonScriptable
{
    /// <summary>
    /// 设置供当前 RDP 连接使用的明文密码。
    /// </summary>
    /// <param name="value">通过 COM BSTR 短暂传入 RDP ActiveX 的账户密码。</param>
    void PutClearTextPassword(IntPtr value);

    /// <summary>
    /// 设置可移植加密凭据密码。
    /// </summary>
    /// <param name="value">可移植密码字符串。</param>
    void PutPortablePassword([In, MarshalAs(UnmanagedType.BStr)] string value);

    /// <summary>
    /// 读取可移植加密凭据密码。
    /// </summary>
    /// <returns>系统返回的可移植密码。</returns>
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetPortablePassword();

    /// <summary>
    /// 设置可移植密码盐值。
    /// </summary>
    /// <param name="value">可移植盐值字符串。</param>
    void PutPortableSalt([In, MarshalAs(UnmanagedType.BStr)] string value);

    /// <summary>
    /// 读取可移植密码盐值。
    /// </summary>
    /// <returns>系统返回的可移植盐值。</returns>
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetPortableSalt();

    /// <summary>
    /// 设置二进制格式密码。
    /// </summary>
    /// <param name="value">二进制格式密码。</param>
    void PutBinaryPassword([In, MarshalAs(UnmanagedType.BStr)] string value);

    /// <summary>
    /// 读取二进制格式密码。
    /// </summary>
    /// <returns>系统返回的二进制格式密码。</returns>
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetBinaryPassword();

    /// <summary>
    /// 设置二进制格式密码盐值。
    /// </summary>
    /// <param name="value">二进制格式盐值。</param>
    void PutBinarySalt([In, MarshalAs(UnmanagedType.BStr)] string value);

    /// <summary>
    /// 读取二进制格式密码盐值。
    /// </summary>
    /// <returns>系统返回的二进制格式盐值。</returns>
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetBinarySalt();

    /// <summary>
    /// 清除 RDP ActiveX 当前持有的登录密码。
    /// </summary>
    void ResetPassword();
}
