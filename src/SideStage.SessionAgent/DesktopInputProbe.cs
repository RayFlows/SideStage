/*
 * 文件名称：DesktopInputProbe.cs
 * 文件职责：在已核验编号的分身代理内测试 Windows 能否接收模拟输入。
 * 输入边界：只对普通交互桌面发送一次相对位移为零的鼠标移动。没有点击、按键或焦点切换。
 */
using System.Runtime.InteropServices;
using System.Text;

namespace SideStage.SessionAgent;

internal static class DesktopInputProbe
{
    /// <summary>
    /// 核对窗口站、代理线程桌面和当前输入桌面，再检查 Windows 实际接纳的事件数。
    /// 访问不到输入桌面或事件数为零，均表示本次未通过。
    /// </summary>
    internal static bool TryZeroMove()
    {
        nint desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
        if (desktop == 0) return false;
        try
        {
            if (!IsNamed(GetProcessWindowStation(), "WinSta0") ||
                !IsNamed(GetThreadDesktop(GetCurrentThreadId()), "Default") || !IsNamed(desktop, "Default"))
                return false;

            MouseInputEvent input = new() { Flags = 1 }; // MOUSEEVENTF_MOVE，dx 和 dy 均为零
            return SendInput(1, ref input, Marshal.SizeOf<MouseInputEvent>()) == 1;
        }
        finally { CloseDesktop(desktop); }
    }

    /// <summary>读取内核对象名称。查询失败时拒绝向未知桌面发送输入。</summary>
    private static bool IsNamed(nint handle, string expected)
    {
        StringBuilder name = new(256);
        return handle != 0 && GetUserObjectInformationW(handle, 2, name, name.Capacity * sizeof(char), out _)
            && string.Equals(name.ToString(), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>本项目仅支持 x64。INPUT 的鼠标联合体从第 8 字节开始，dwFlags 位于第 20 字节。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MouseInputEvent { [FieldOffset(20)] internal uint Flags; }

    /// <summary>打开当前接收输入的桌面，只申请读取对象名称的权限。</summary>
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    /// <summary>归还输入桌面句柄。</summary>
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    /// <summary>取得代理进程绑定的窗口站。</summary>
    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    /// <summary>取得调用线程编号。</summary>
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    /// <summary>取得指定线程当前绑定的桌面。</summary>
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    /// <summary>查询窗口站或桌面的 Unicode 名称。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, int length, out int needed);
    /// <summary>投递一个鼠标事件，返回 Windows 实际接纳的事件数。</summary>
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, ref MouseInputEvent input, int size);
}
