/*
 * 文件名称：ParchmentPreviewForm.Close.cs
 * 文件职责：统一窗口关闭键、Alt+F4 与任务栏关闭命令的确认流程。
 * 操作边界：用户确认前保留当前连接。只有本次连接已绑定且 Windows 仍可核验的子会话才可注销。
 * 失败处理：注销失败或目标变化时保留窗口并记录错误，供用户处置。
 */
using SideStage.Core.Sessions;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App;

internal sealed partial class ParchmentPreviewForm
{
    private bool _confirmingClose;

    /// <summary>处理用户关闭窗口的请求。暂停状态轮询，防止确认期间自动重连改变目标。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || e.CloseReason != CloseReason.UserClosing) return;
        if (_changingStartupItems)
        {
            e.Cancel = true;
            AppendLog("正在处理分身启动项，完成后即可关闭窗口。");
            return;
        }
        // 独立更新路径已经等到 ActiveX 断开；绝不能再转入下面的注销确认。
        if (_exitingForUpdate) return;
        if (_preparingUpdate) { e.Cancel = true; return; }
        if (_confirmingClose) { e.Cancel = true; return; }
        _confirmingClose = true;
        CancelParentRecovery();
        try
        {
            e.Cancel = !ConfirmPreviewClose(ReadCloseState, AskClose, _sessionTerminator.LogoffCurrentChildSession);
            if (!e.Cancel) _audioReconnect.Cancel();
        }
        catch (Exception ex) when (IsDisplayable(ex))
        {
            e.Cancel = true;
            ShowError("关闭窗口失败", ex);
        }
        finally { _confirmingClose = false; }
    }

    /// <summary>读取此窗口的连接状态和绑定目标，检查子会话仍归属于当前主桌面。</summary>
    private (bool Active, SessionId? Target) ReadCloseState()
    {
        var control = _previewPanel.PreviewControl;
        var state = control.GetConnectionState();
        if (state == RdpPreviewConnectionState.Disconnected && !_audioReconnect.Active) return (false, null);
        if (state == RdpPreviewConnectionState.Connected && control.VerifiedConnectionTarget == null)
        {
            try { control.VerifyConnectedChildSession(); }
            catch (InvalidOperationException) { control.CanPresentChildSignIn(); }
        }
        if (control.VerifiedConnectionTarget is not { } bound) return (true, null);
        var child = _sessionInspector.TryGetChildSession();
        return (true, child is { IsChildSession: true, StateQueryError: null } &&
            child.Id == bound && child.Id != _sessionInspector.GetCurrentSession().Id ? bound : null);
    }

    /// <summary>在确认前后比对同一份意图。无法确定目标时只允许用户明确选择关闭预览。</summary>
    internal static bool ConfirmPreviewClose(Func<(bool Active, SessionId? Target)> read,
        Func<SessionId?, bool> ask, Action<SessionId> logoff)
    {
        var before = read();
        if (!before.Active) return true;
        if (!ask(before.Target)) return false;
        if (before.Target is not { } id) return true;
        if (read() != before) throw new InvalidOperationException("连接状态或目标子会话已经变化，请重新关闭窗口并确认。");
        logoff(id);
        return true;
    }

    /// <summary>默认选择取消，告知用户注销的确切目标和未保存工作的后果。</summary>
    private bool AskClose(SessionId? target)
    {
        string message = target is { } id
            ? $"注销子会话 {id.Value} 并关闭窗口？\r\n\r\n子会话中的程序将全部结束，未保存的工作会丢失。"
            : "当前连接尚无可核验的注销目标。仅断开连接并关闭窗口，保留子会话？";
        return MessageBox.Show(this, message, $"关闭 {Program.DisplayName}", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}
