/*
 * 文件职责：通过 SideStage 的真实设置开关复现快速连点和音频重连，检查最终登录的分身编号。
 * 测试边界：仅使用本机已保存的凭据，退出时断开测试连接并保留子会话。
 */
using System.Runtime.InteropServices;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App.FrameCheck;

internal static class AudioReconnectCheck
{
    /// <summary>连续切换两种声音模式，检查连点互斥、原会话恢复以及用户断开优先。</summary>
    internal static int Run(ParchmentPreviewForm window)
    {
        Control[] controls = Descendants(window).ToArray();
        var preview = controls.OfType<ChildSessionPreviewPanel>().Single().PreviewControl;
        var mute = controls.OfType<CheckBox>().Single(c => c.Text == "静音分身");
        var connect = controls.OfType<Button>().Single(b => b.Text == "连接");
        bool Ready() => preview.LoginCompleted && preview.Enabled &&
            preview.GetConnectionState() == RdpPreviewConnectionState.Connected;
        try
        {
            connect.PerformClick();
            Wait(() => Ready() && mute.Enabled && preview.VerifiedConnectionTarget != null, 25000);
            var target = preview.VerifyConnectedChildSession().Id;
            foreach (bool muted in new[] { false, true })
            {
                OpenSettings(window, mute);
                mute.Checked = muted;
                if (mute.Enabled) throw new InvalidOperationException("断开发出后，开关仍接受第二次点击。");
                // 即便自动化或排队的事件绕过禁用外观，重复的 CheckedChanged 也不能重新发起断开。
                for (int i = 0; i < 9; i++) mute.Checked = !mute.Checked;
                Wait(() => mute.Enabled && Ready() && preview.VerifyConnectedChildSession().Id == target, 30000);
                if (mute.Checked != muted || (preview.AudioMode == ChildSessionAudioMode.DoNotPlay) != muted)
                    throw new InvalidOperationException("切换后的声音模式与请求不符。");
                Console.WriteLine($"已恢复原分身 {target}，静音={muted}。");
            }
            OpenSettings(window, mute);
            mute.Checked = false;
            controls.OfType<Button>().Single(b => b.Text == "断开连接").PerformClick();
            Wait(() => connect.Enabled && preview.GetConnectionState() == RdpPreviewConnectionState.Disconnected, 8000);
            long until = Environment.TickCount64 + 3000;
            Wait(() => Environment.TickCount64 >= until, 5000);
            if (preview.GetConnectionState() != RdpPreviewConnectionState.Disconnected)
                throw new InvalidOperationException("手动断开之后仍发生了音频重连。");
            Console.WriteLine("用户在切换期间取消，预览保持断开。");
            return 0;
        }
        finally
        {
            if (preview.GetConnectionState() != RdpPreviewConnectionState.Disconnected)
            {
                // 正在断开时再次调用系统控件会失败，留出完成旧请求的时间。
                long settle = Environment.TickCount64 + 600;
                Wait(() => Environment.TickCount64 >= settle, 2500);
                if (preview.GetConnectionState() != RdpPreviewConnectionState.Disconnected)
                    preview.DisconnectPreview();
            }
        }
    }

    /// <summary>通过宿主已有的设置菜单打开侧栏，确认测试操作处于用户可访问的界面。</summary>
    private static void OpenSettings(Form window, CheckBox mute)
    {
        if (!mute.Visible) SendMessageW(window.Handle, 0x0112, 0x1200, 0);
        if (!mute.Visible || !mute.Enabled) throw new InvalidOperationException("无法操作静音开关。");
    }

    /// <summary>分派窗口消息并等待指定状态，避免阻塞负责 ActiveX 通知的界面线程。</summary>
    private static void Wait(Func<bool> condition, int timeout)
    {
        long until = Environment.TickCount64 + timeout;
        while (!condition())
        {
            if (Environment.TickCount64 >= until) throw new TimeoutException("音频切换未在期限内达到预期状态。");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    /// <summary>递归枚举验收窗口中真实的控件和按钮。</summary>
    private static IEnumerable<Control> Descendants(Control control) =>
        control.Controls.Cast<Control>().SelectMany(Descendants).Prepend(control);

    /// <summary>向本测试拥有的窗口发送设置菜单命令。</summary>
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, int message, nint w, nint l);
}
