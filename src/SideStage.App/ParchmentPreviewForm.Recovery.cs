/*
 * 文件名称：ParchmentPreviewForm.Recovery.cs
 * 文件职责：管理高级设置中的父桌面恢复开关、Windows 会话通知，以及单次恢复的生命周期。
 * 操作边界：只有本窗口连接过的分身可以参与恢复。主动锁屏、关闭分身连接、关闭开关都会解除待恢复目标。
 */
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using SideStage.Core.Sessions;
using SideStage.Windows.Platform.Windows;

namespace SideStage.App;

internal sealed partial class ParchmentPreviewForm
{
    private const string RecoveryPreference = "RecoverDisconnectedParentToConsole";
    private const string RecoveryIssueUrl = "https://github.com/RayFlows/SideStage/issues/5";
    private const string RecoveryFooter = "如果您不知道这是在干什么，请保持开启按钮。";
    private const string RecoveryNote = "远程桌面断开且分身输入失效时，尝试将主桌面接回空闲的本机控制台，再检查分身输入。接回后，本机显示器可能直接显示你的桌面。主动锁屏会保留锁定状态。此功能需要管理员权限。";
    private const string RecoveryPrivilegeHint = "未获取管理员权限，无法启用";
    private readonly bool _watchSessionChanges;
    private readonly ParentDisconnectWatch _disconnectWatch = new();
    private SettingsSwitch? _recoverParent;
    private PaperInfoButton? _recoveryWarning;
    private bool _recoveryRequested, _recoveryAllowed;
    private bool _registeredForSessions;
    private int _observedSessionId;
    private CancellationTokenSource? _pendingRecovery;

    /// <summary>把说明放在开关紧邻的分组标题右端，鼠标、触控和键盘均可打开。</summary>
    private void AddRecoverySettings(TableLayoutPanel options)
    {
        TableLayoutPanel heading = new() { Dock = DockStyle.Top, Margin = Padding.Empty, AutoSize = true, ColumnCount = 3 };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        DrawerMetrics.Margin(heading, 0, SectionGap, 0, 0);
        heading.Controls.Add(SectionHeading("主桌面恢复", compact: true), 0, 0);
        _recoveryWarning = new PaperInfoButton(_drawerArrowFont, _labelFont, "主桌面恢复权限提示", RecoveryPrivilegeHint)
        {
            Text = "\uE7BA", ForeColor = Color.FromArgb(156, 109, 26), Visible = false
        };
        heading.Controls.Add(_recoveryWarning, 1, 0);
        heading.Controls.Add(new PaperInfoButton(_drawerArrowFont, _labelFont, "会话恢复说明", RecoveryNote,
            "查看 Issue #5", OpenRecoveryIssue, RecoveryFooter), 2, 0);
        options.Controls.Add(heading);
        var list = new SettingsList();
        _recoverParent = new SettingsSwitch("远程断开后接回主桌面", _bodyFont) { Enabled = false };
        try { _recoveryRequested = ReadRecoveryPreference(_updatePreferenceKey); }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
        {
            Shown += (_, _) => AppendLog("无法读取会话恢复设置，本次启动已关闭自动接回。", LogLevel.Warning);
        }
        _recoverParent.CheckedChanged += ChangeRecoveryPreference;
        list.AddRow(_recoverParent);
        options.Controls.Add(list);
    }

    /// <summary>按当前令牌更新开关与警示。临时限制只影响本次运行，保留用户已保存的偏好。</summary>
    private void ApplyRecoveryPrivilege(bool allowed)
    {
        _recoveryAllowed = allowed;
        if (_recoverParent == null || _recoveryWarning == null) return;
        _recoverParent.CheckedChanged -= ChangeRecoveryPreference;
        _recoverParent.Enabled = allowed;
        _recoverParent.Checked = allowed && _recoveryRequested;
        _recoverParent.AccessibleDescription = allowed ? null : RecoveryPrivilegeHint;
        _recoverParent.CheckedChanged += ChangeRecoveryPreference;
        _recoveryWarning.Visible = !allowed;
        if (!allowed) CancelParentRecovery();
    }

    /// <summary>用户激活链接时用默认浏览器打开固定的 GitHub 议题，系统未配置浏览器时记录原因。</summary>
    private void OpenRecoveryIssue()
    {
        try { using var browser = Process.Start(new ProcessStartInfo(RecoveryIssueUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (IsDisplayable(ex)) { AppendLog($"无法打开 Issue #5：{ex.Message}", LogLevel.Warning); }
    }

    /// <summary>第一次运行默认开启。只认 DWORD 的 1 为显式启用，损坏的值按关闭处理。</summary>
    internal static bool ReadRecoveryPreference(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(RecoveryPreference) is null or 1;
    }

    /// <summary>向当前用户的设置项保存选择。验收使用独立键，不改动实际偏好。</summary>
    internal static void WriteRecoveryPreference(string path, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(RecoveryPreference, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>关闭立即取消本次等待，重新开启仅对以后的远程断开生效。</summary>
    private void ChangeRecoveryPreference(object? sender, EventArgs e)
    {
        if (!_recoveryAllowed) return;
        _recoveryRequested = _recoverParent!.Checked;
        CancelParentRecovery();
        try { WriteRecoveryPreference(_updatePreferenceKey, _recoveryRequested); }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
        {
            AppendLog("会话恢复开关在本次运行中已生效，但未能保存。", LogLevel.Warning);
        }
    }

    /// <summary>订阅本进程所在会话的通知，避免把其他用户或子桌面自己的断开当成触发条件。</summary>
    private void RegisterSessionChanges()
    {
        if (!_watchSessionChanges) return;
        using var own = Process.GetCurrentProcess();
        _observedSessionId = own.SessionId;
        _registeredForSessions = WTSRegisterSessionNotification(Handle, 0);
        if (!_registeredForSessions)
        {
            int error = Marshal.GetLastPInvokeError();
            BeginInvoke(() => AppendLog($"无法监听远程断开，会话恢复不可用（Windows {error}）。", LogLevel.Warning));
        }
    }

    /// <summary>根据本会话的 WTS 消息消费一次恢复资格。控制台接回本身产生的通知不打断输入复检。</summary>
    private void OnSessionChange(nint reason, nint session)
    {
        if (!_registeredForSessions || session != _observedSessionId) return;
        switch ((int)reason)
        {
            case 3: // WTS_REMOTE_CONNECT，用户重新从远端连入。
                CancelParentRecovery();
                _disconnectWatch.OnRemoteConnect();
                break;
            case 4: // WTS_REMOTE_DISCONNECT，与锁屏、关闭预览完全不同。
                var child = _disconnectWatch.TakeDisconnect();
                if (_recoveryAllowed && _recoverParent?.Checked == true && !_confirmingClose && !_preparingUpdate && child.HasValue)
                    _ = RecoverParentAsync(child.Value);
                break;
            case 7: // WTS_SESSION_LOCK，锁屏不能单独触发恢复。
                // RDP 断开也可能同时产生锁屏通知。系统若已报告断开，保留上次活动绑定，
                // 仍须等到独立的远程断开通知；仅在活动桌面按 Win+L 则立即清除绑定。
                try
                {
                    if (_sessionInspector.GetCurrentSession().ConnectionState != SessionConnectionState.Disconnected)
                        _disconnectWatch.OnLock();
                }
                catch (Exception ex) when (IsDisplayable(ex)) { _disconnectWatch.OnLock(); }
                break;
            case 8: // WTS_SESSION_UNLOCK，等待下一次活动父子会话核验。
                _disconnectWatch.OnUnlock();
                break;
            case 2: // WTS_CONSOLE_DISCONNECT
            case 6: // WTS_SESSION_LOGOFF
                CancelParentRecovery();
                break;
        }
    }

    /// <summary>每次断开只创建一个可取消的后台操作，返回 UI 线程后记录原分身编号和结果。</summary>
    private async Task RecoverParentAsync(SessionId child)
    {
        using var lifetime = new CancellationTokenSource();
        _pendingRecovery?.Cancel();
        _pendingRecovery = lifetime;
        try
        {
            var result = await WindowsParentSessionRecovery.RecoverAsync(child, lifetime.Token);
            if (!lifetime.IsCancellationRequested && !IsDisposed)
                AppendLog($"分身 {child.Value}：{result.Detail}", result.InputReady ? LogLevel.Info : LogLevel.Warning);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested && !IsDisposed)
                AppendLog($"分身 {child.Value} 未能自动接回主桌面：{ex.Message}", LogLevel.Warning);
        }
        finally { if (ReferenceEquals(_pendingRecovery, lifetime)) _pendingRecovery = null; }
    }

    /// <summary>清除尚未消费的分身编号，并取消执行中的等待或复检。</summary>
    private void CancelParentRecovery()
    {
        _disconnectWatch.ClearTarget();
        _pendingRecovery?.Cancel();
    }

    /// <summary>销毁窗口句柄时解除订阅，后续新句柄将独立注册。</summary>
    private void UnregisterSessionChanges()
    {
        CancelParentRecovery();
        if (_registeredForSessions) WTSUnRegisterSessionNotification(Handle);
        _registeredForSessions = false;
    }

    /// <summary>让 Windows 向本窗口发送 WM_WTSSESSION_CHANGE。</summary>
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    /// <summary>注销当前 HWND 的会话消息订阅。</summary>
    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern bool WTSUnRegisterSessionNotification(nint window);
}

/// <summary>只消费前一次已核验的远程连接，防止消息重放、主动锁屏或重新打开开关导致追溯恢复。</summary>
internal sealed class ParentDisconnectWatch
{
    private SessionId? _child;
    private bool _locked, _consumed;

    /// <summary>仅在主桌面仍然活动时更新记录，保留断开通知抵达前最后一次核验的目标。</summary>
    internal void Observe(SessionSnapshot parent, SessionId? verifiedChild)
    {
        if (parent.ConnectionState != SessionConnectionState.Disconnected)
            _child = !_consumed && !_locked && parent.IsDesktopReady ? verifiedChild : null;
    }

    /// <summary>取出并消费目标；同一轮的后续重复通知一律返回空。</summary>
    internal SessionId? TakeDisconnect()
    {
        var target = _child;
        _consumed = true;
        _child = null;
        return target;
    }

    /// <summary>清除原分身绑定，下次活动状态采样后才能取得新的绑定。</summary>
    internal void ClearTarget() => _child = null;
    /// <summary>锁屏立即丢弃目标，等待用户解锁。</summary>
    internal void OnLock() { _locked = true; ClearTarget(); }
    /// <summary>解锁本身不授予恢复资格，仍需重新核验分身。</summary>
    internal void OnUnlock() => _locked = false;
    /// <summary>新的远程连接开始新的通知周期。</summary>
    internal void OnRemoteConnect() { _consumed = false; _locked = false; ClearTarget(); }
}
