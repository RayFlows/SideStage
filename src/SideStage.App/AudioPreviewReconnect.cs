/*
 * 文件职责：协调两个预览窗口的音频切换。一次切换只断开一次，且只重连原有子会话。
 * 生命周期：在界面线程接收用户操作和状态轮询，等待 ActiveX 与 Windows 会话均断开，
 *   再协商新音频模式。手动断开、关闭或注销可取消尚未发出的重连。
 */
using SideStage.Core.Sessions;
using SideStage.Windows.Platform.Windows;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App;

internal sealed class AudioPreviewReconnect
{
    private enum Phase { Idle, RequestingDisconnect, WaitingForDisconnect, WaitingForLogin }
    private Phase _phase;
    private SessionId _target;
    private long _started, _stableSince;
    private int _attempts;
    internal bool Active => _phase != Phase.Idle;

    /// <summary>在断开预览时直接保存模式；在活动预览上先占用切换状态，再请求一次断开。</summary>
    internal void Change(ChildSessionPreviewControl preview, ChildSessionAudioMode mode)
    {
        if (Active || preview.AudioMode == mode) return;
        if (preview.GetConnectionState() == RdpPreviewConnectionState.Disconnected)
        {
            preview.SetAudioMode(mode);
            return;
        }
        _target = preview.VerifyConnectedChildSession().Id;
        _started = Environment.TickCount64;
        _stableSince = 0;
        _attempts = 0;
        _phase = Phase.RequestingDisconnect;
        try
        {
            _phase = preview.RequestAudioModeChange(mode) ? Phase.WaitingForDisconnect : Phase.Idle;
        }
        catch
        {
            Cancel();
            throw;
        }
    }

    /// <summary>取消自动重连。返回真表示旧连接的断开请求已经发出，调用方应跳过重复断开。</summary>
    internal bool Cancel()
    {
        bool disconnectIssued = _phase is Phase.RequestingDisconnect or Phase.WaitingForDisconnect;
        _phase = Phase.Idle;
        return disconnectIssued;
    }

    /// <summary>推进一次音频切换，返回用户可读的进度。失败时释放开关，并由窗口记录原因。</summary>
    internal string? Poll(ChildSessionPreviewControl preview, SessionSnapshot? child,
        WindowsChildSessionCredentialStore vault)
    {
        if (_phase is Phase.Idle or Phase.RequestingDisconnect) return null;
        try
        {
            if (child is null || child.Id != _target)
                throw new InvalidOperationException("原有子会话已结束或发生变化，已停止音频重连。");
            long now = Environment.TickCount64;
            var state = preview.GetConnectionState();
            if (_phase == Phase.WaitingForLogin && state == RdpPreviewConnectionState.Connected &&
                child.IsDesktopReady && preview.VerifyConnectedChildSession().Id == _target)
            {
                Cancel();
                return "音频切换完成，已重新连接原有子会话。";
            }
            if (now - _started >= 30000)
                throw new InvalidOperationException("等待子会话切换声音超时，请检查登录界面或手动连接。");

            if (_phase == Phase.WaitingForLogin)
            {
                if (state != RdpPreviewConnectionState.Disconnected || preview.DisconnectReason == null) return null;
                // 服务端可能在旧连接收尾期间关闭第一次新连接。只对这一种情况退避并重试一次。
                if (_attempts >= 2 || preview.DisconnectReason != 3 || preview.FatalError.HasValue)
                    throw new InvalidOperationException($"音频重连已断开，RDP 原因码 {preview.DisconnectReason}。");
                _phase = Phase.WaitingForDisconnect;
                _stableSince = 0;
            }
            if (state != RdpPreviewConnectionState.Disconnected || preview.DisconnectReason == null ||
                child.ConnectionState != SessionConnectionState.Disconnected)
            {
                _stableSince = 0;
                return null;
            }
            // 两边均报告断开后留出稳定期，防止旧传输的收尾通知落到下一次握手。
            if (_stableSince == 0) _stableSince = now;
            if (now - _stableSince < (_attempts == 0 ? 1000 : 2000)) return null;
            using var saved = vault.Read();
            _attempts++;
            _phase = Phase.WaitingForLogin;
            preview.ReconnectPreview(_target, saved);
            return $"正在以新音频模式重连子会话 {_target.Value}（第 {_attempts} 次）。";
        }
        catch
        {
            Cancel();
            throw;
        }
    }
}
