/*
 * 文件名称：IMsRdpExtendedSettings.cs
 * 文件职责：
 *   声明微软 RDP ActiveX 类型库中的 IMsRdpExtendedSettings COM 接口。
 *
 * 来源与兼容性：
 *   - IID 和方法顺序对应系统 mstscax.dll 类型库中的 IMsRdpExtendedSettings。
 *   - 该接口使用 IUnknown vtable，不是 ActiveX 的 IDispatch 普通属性集合。
 *   - 方法必须按原生 vtable 顺序声明；改变顺序会导致 COM 内存调用错误。
 *
 * 维护要求：
 *   Windows RDP ActiveX 升级后，应重新核对系统类型库中的 IID、vtable 顺序和 VARIANT 签名。
 */

using System.Runtime.InteropServices;

namespace SideStage.Windows.RemoteDesktop.Interop;

/// <summary>
/// 用于设置或读取微软 RDP ActiveX 扩展属性的原生 COM 接口。
/// </summary>
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpExtendedSettings
{
    /// <summary>
    /// 设置一个由 RDP ActiveX 定义的扩展属性。
    /// </summary>
    /// <param name="propertyName">扩展属性的名称。</param>
    /// <param name="value">以 COM VARIANT 传递的属性值。</param>
    void SetProperty(
        [In, MarshalAs(UnmanagedType.BStr)] string propertyName,
        [In, Out, MarshalAs(UnmanagedType.Struct)] ref object value);

    /// <summary>
    /// 读取一个由 RDP ActiveX 定义的扩展属性。
    /// </summary>
    /// <param name="propertyName">扩展属性的名称。</param>
    /// <param name="value">Windows 返回的属性值。</param>
    void GetProperty(
        [In, MarshalAs(UnmanagedType.BStr)] string propertyName,
        [Out, MarshalAs(UnmanagedType.Struct)] out object value);
}
