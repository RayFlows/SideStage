/*
 * 文件名称：WindowsSessionInspector.cs
 * 文件职责：
 *   使用 Windows Remote Desktop Services 原生 API 实现会话观察接口。
 *
 * 设计边界：
 *   - 本类只读取系统状态，不创建、连接、断开或结束会话。
 *   - 所有原生调用集中在本文件，Core 上层只看到 SessionSnapshot。
 *   - 子会话已有编号但详情暂不可读时，在快照保留 Win32 错误码；其他失败抛出异常。
 *   - Windows 返回“没有子会话”的哨兵值时，才返回 null。
 *
 * 线程安全：
 *   本类不保存可变会话状态，多个线程可以共享同一个实例。
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SideStage.Core.Sessions;

namespace SideStage.Core.Platform.Windows;

/// <summary>
/// 基于 wtsapi32.dll 的 Windows 会话观察器。
/// </summary>
public sealed class WindowsSessionInspector : ISessionInspector
{
    private const uint NoChildSession = uint.MaxValue;
    private const int ErrorNotFound = 1168;

    /// <summary>
    /// 查询当前进程所属的 Windows 会话。
    /// </summary>
    /// <returns>当前会话的状态快照。</returns>
    /// <exception cref="Win32Exception">
    /// 当前进程会话编号或会话连接状态无法读取时抛出。
    /// </exception>
    public SessionSnapshot GetCurrentSession()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        SessionId sessionId = CreateSessionId((uint)currentProcess.SessionId, "Process.SessionId");

        return ReadSnapshot(sessionId, isChildSession: false);
    }

    /// <summary>
    /// 查询当前父会话是否存在子会话，并读取子会话状态。
    /// </summary>
    /// <returns>
    /// 存在子会话时返回其快照；Windows 返回无子会话哨兵值时返回 null。
    /// </returns>
    /// <exception cref="Win32Exception">
    /// Windows 无法返回子会话编号或子会话状态时抛出。
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Windows 返回无效会话编号时抛出。
    /// </exception>
    public SessionSnapshot? TryGetChildSession()
    {
        bool succeeded = WTSGetChildSessionId(out uint nativeSessionId);
        if (!succeeded)
        {
            // Windows 在没有子会话时返回 ERROR_NOT_FOUND；此时输出参数内容未定义，
            // 因此不能再要求它同时等于 0xFFFFFFFF。
            int errorCode = Marshal.GetLastPInvokeError();
            if (errorCode == ErrorNotFound)
            {
                return null;
            }

            throw CreateWin32Exception(nameof(WTSGetChildSessionId), errorCode);
        }

        // Windows 用 0xFFFFFFFF 表示当前父会话还没有子会话。
        if (nativeSessionId == NoChildSession)
        {
            return null;
        }

        SessionId sessionId = CreateSessionId(nativeSessionId, nameof(WTSGetChildSessionId));
        return ReadSnapshot(sessionId, isChildSession: true);
    }

    /// <summary>
    /// 查询 Windows 是否启用了子会话能力。
    /// </summary>
    /// <returns>启用时返回 true，否则返回 false。</returns>
    /// <exception cref="Win32Exception">
    /// Windows 无法返回能力开关时抛出。
    /// </exception>
    public bool IsChildSessionsEnabled()
    {
        if (!WTSIsChildSessionsEnabled(out int enabled))
        {
            throw CreateWin32Exception(nameof(WTSIsChildSessionsEnabled));
        }

        return enabled != 0;
    }

    /// <summary>
    /// 读取指定会话的连接状态，并组装不可变快照。
    /// </summary>
    /// <param name="sessionId">需要读取的有效会话编号。</param>
    /// <param name="isChildSession">该会话是否来自子会话查询。</param>
    /// <returns>包含采集时间和连接状态的快照。</returns>
    /// <exception cref="Win32Exception">
    /// WTSQuerySessionInformationW 调用失败时抛出。
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Windows 成功返回，但会话状态缓冲区为空或长度不足时抛出。
    /// </exception>
    private static SessionSnapshot ReadSnapshot(SessionId sessionId, bool isChildSession)
    {
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (!WTSQuerySessionInformationW(
                    IntPtr.Zero,
                    sessionId.Value,
                    NativeWtsInfoClass.WtsSessionInfoEx,
                    out buffer,
                    out uint bytesReturned))
            {
                return HandleSnapshotQueryError(sessionId, isChildSession, Marshal.GetLastPInvokeError(), ReadBasicState);
            }

            // WTSINFOEXW 的一级数据包含会话编号、连接状态和锁定标志，来自同一次系统采样。
            if (buffer == IntPtr.Zero || bytesReturned < Marshal.SizeOf<SessionInfoPrefix>())
            {
                throw new InvalidDataException(
                    $"{nameof(WTSQuerySessionInformationW)} 返回了无效会话状态缓冲区（字节数：{bytesReturned}）。");
            }

            SessionInfoPrefix info = Marshal.PtrToStructure<SessionInfoPrefix>(buffer);
            if (info.Level != 1 || info.Session != sessionId.Value)
            {
                throw new InvalidDataException("WTSInfoEx 的级别或返回的会话编号与请求不符。");
            }

            return new SessionSnapshot(
                sessionId,
                MapConnectionState(info.State),
                isChildSession,
                DateTimeOffset.UtcNow)
            {
                IsLocked = info.Flags switch { 0 => true, 1 => false, _ => null },
            };
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                // WTSQuerySessionInformationW 分配的缓冲区必须由 WTSFreeMemory 释放。
                WTSFreeMemory(buffer);
            }
        }
    }

    /// <summary>
    /// 区分子会话详情暂不可读和必须上报的查询失败。
    /// 保留 Windows 已报告的编号和原始错误码，供宿主显示并重试连接。
    /// </summary>
    /// <param name="sessionId">本次查询的会话编号。</param>
    /// <param name="isChildSession">编号是否来自当前父会话的子会话查询。</param>
    /// <param name="errorCode">扩展状态查询失败时立即保存的 Win32 错误码。</param>
    /// <param name="readBasicState">需要区分登录阶段和失效编号时，对同一编号发起基础查询。</param>
    /// <returns>保留查询诊断的子会话快照，锁定状态始终未知。</returns>
    /// <exception cref="Win32Exception">父会话查询失败，或 Windows 返回其他错误。</exception>
    internal static SessionSnapshot HandleSnapshotQueryError(
        SessionId sessionId, bool isChildSession, int errorCode,
        Func<SessionId, (SessionConnectionState State, int? Error)> readBasicState)
    {
        // 分身切换期间编号可能仍在，但查询不到对应的会话；本机此时 WTSInfoEx 返回 1733。
        if (!isChildSession || errorCode is not (2 or 5 or 1733 or 7022))
        {
            throw CreateWin32Exception(nameof(WTSQuerySessionInformationW), errorCode);
        }

        // 扩展查询在登录前也可能返回相同的 1733。只补查连接状态，不推测桌面锁定标志。
        (SessionConnectionState state, int? basicError) = errorCode == 1733
            ? readBasicState(sessionId)
            : (SessionConnectionState.Unknown, null);
        return new SessionSnapshot(sessionId, state, true, DateTimeOffset.UtcNow)
        {
            StateQueryError = errorCode,
            BasicStateQueryError = basicError,
        };
    }

    /// <summary>
    /// 读取单个 WTS_CONNECTSTATE_CLASS 数值；失败时保留原始错误，以区分编号失效与查询受限。
    /// </summary>
    /// <param name="sessionId">扩展查询使用的同一子会话编号。</param>
    /// <returns>基础连接状态及可选的 Win32 错误码。</returns>
    private static (SessionConnectionState State, int? Error) ReadBasicState(SessionId sessionId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId.Value, NativeWtsInfoClass.WtsConnectState,
                out buffer, out uint bytesReturned))
            {
                return (SessionConnectionState.Unknown, Marshal.GetLastPInvokeError());
            }

            if (buffer == IntPtr.Zero || bytesReturned < sizeof(int))
            {
                throw new InvalidDataException("Windows 返回的基础连接状态缓冲区无效。");
            }

            return (MapConnectionState(Marshal.ReadInt32(buffer)), null);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    /// <summary>
    /// 将 Windows 原生会话编号转换为公开的强类型编号。
    /// </summary>
    /// <param name="nativeSessionId">Windows 返回的无符号会话编号。</param>
    /// <param name="sourceName">产生该编号的 API 名称。</param>
    /// <returns>有效的 SessionId。</returns>
    /// <exception cref="InvalidOperationException">
    /// Windows 返回了无法使用的会话编号时抛出。
    /// </exception>
    private static SessionId CreateSessionId(uint nativeSessionId, string sourceName)
    {
        if (nativeSessionId == NoChildSession)
        {
            throw new InvalidOperationException(
                $"{sourceName} 返回了无效会话编号 0xFFFFFFFF。");
        }

        return new SessionId(nativeSessionId);
    }

    /// <summary>
    /// 把 WTS_CONNECTSTATE_CLASS 的数值映射到库内稳定枚举。
    /// </summary>
    /// <param name="nativeState">Windows 返回的原生状态数值。</param>
    /// <returns>对应的公开连接状态；未知值映射为 Unknown。</returns>
    private static SessionConnectionState MapConnectionState(int nativeState)
    {
        return nativeState switch
        {
            0 => SessionConnectionState.Active,
            1 => SessionConnectionState.Connected,
            2 => SessionConnectionState.ConnectQuery,
            3 => SessionConnectionState.Shadow,
            4 => SessionConnectionState.Disconnected,
            5 => SessionConnectionState.Idle,
            6 => SessionConnectionState.Listen,
            7 => SessionConnectionState.Reset,
            8 => SessionConnectionState.Down,
            9 => SessionConnectionState.Init,
            _ => SessionConnectionState.Unknown,
        };
    }

    /// <summary>
    /// 根据最后一个 Win32 错误码构造包含 API 名称的异常。
    /// </summary>
    /// <param name="apiName">失败的 Windows API 名称。</param>
    /// <returns>包含原始错误码和可读描述的 Win32Exception。</returns>
    private static Win32Exception CreateWin32Exception(string apiName)
    {
        int errorCode = Marshal.GetLastPInvokeError();
        return CreateWin32Exception(apiName, errorCode);
    }

    /// <summary>
    /// 使用已经捕获的原生错误码构造异常，避免后续调用覆盖线程的最后错误状态。
    /// </summary>
    /// <param name="apiName">失败的 Windows API 名称。</param>
    /// <param name="errorCode">API 失败后立即捕获的 Win32 错误码。</param>
    /// <returns>包含原始错误码和可读描述的 Win32Exception。</returns>
    private static Win32Exception CreateWin32Exception(string apiName, int errorCode)
    {
        return new Win32Exception(
            errorCode,
            $"{apiName} 调用失败（Win32 {errorCode}）：{new Win32Exception(errorCode).Message}");
    }

    /// <summary>
    /// WTS_CONNECTSTATE_CLASS 的受控映射入口。
    /// </summary>
    private enum NativeWtsInfoClass
    {
        /// <summary>读取单个连接状态枚举，不包含锁定标志。</summary>
        WtsConnectState = 8,

        /// <summary>返回 WTSINFOEXW，可以同时区分连接状态和桌面锁定状态。</summary>
        WtsSessionInfoEx = 25,
    }

    /// <summary>
    /// Windows x64 的 WTSINFOEXW 一级结构前缀。数据联合体含 LARGE_INTEGER，
    /// 以八字节对齐开始；只映射所需的四个字段，不读取用户信息。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct SessionInfoPrefix
    {
        [FieldOffset(0)] public uint Level;
        [FieldOffset(8)] public uint Session;
        [FieldOffset(12)] public int State;
        [FieldOffset(16)] public int Flags;
    }

    /// <summary>
    /// 查询 Windows 子会话能力开关。
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSIsChildSessionsEnabled(out int enabled);

    /// <summary>
    /// 读取当前父会话关联的子会话编号。
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSGetChildSessionId(out uint sessionId);

    /// <summary>
    /// 读取指定会话的连接状态缓冲区。
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr serverHandle,
        uint sessionId,
        NativeWtsInfoClass informationClass,
        out IntPtr buffer,
        out uint bytesReturned);

    /// <summary>
    /// 释放由 WTS 查询 API 分配的内存。
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr memory);
}
