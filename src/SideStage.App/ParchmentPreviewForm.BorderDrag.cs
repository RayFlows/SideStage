/*
 * 文件名称：ParchmentPreviewForm.BorderDrag.cs
 * 文件职责：连接后从按下铜边到松开鼠标，以屏幕像素计算真实窗口的新位置和大小。
 * 生命周期：一次拖动独占鼠标捕获。停止移动时恢复实时远程画面，松手、失焦及取消时归还输入。
 */
namespace SideStage.App;

internal sealed partial class ParchmentPreviewForm
{
    private sealed class PreviewBorderDrag : IMessageFilter, IDisposable
    {
        private readonly ParchmentPreviewForm _host;
        private readonly Rectangle _initial;
        private readonly Point _start;
        private readonly int _edge;
        private readonly System.Windows.Forms.Timer _idle = new() { Interval = 100 };
        private bool _ended;

        /// <summary>固定按下时的矩形和鼠标坐标。捕获期间，来自设置或远程控件的 Esc 同样能取消。</summary>
        internal PreviewBorderDrag(ParchmentPreviewForm host, int edge, Point start)
        {
            _host = host;
            _edge = edge;
            _start = start;
            _initial = host.Bounds;
            _idle.Tick += RestoreLiveContent;
            Application.AddMessageFilter(this);
        }

        /// <summary>鼠标停住后撤下过渡帧，让持续变化的远程画面自然播放。</summary>
        private void RestoreLiveContent(object? sender, EventArgs args)
        {
            _idle.Stop();
            _host.RestorePaintedRim();
        }

        /// <summary>只在持有抓手时消费 Esc，不向远程会话发送取消键。</summary>
        public bool PreFilterMessage(ref Message message)
        {
            if (message.Msg != 0x0100 || message.WParam != 27) return false;
            Finish(cancel: true);
            return true;
        }

        /// <summary>处理捕获期间的客户区鼠标消息和系统取消通知，其他消息继续走窗口原有逻辑。</summary>
        internal bool HandleMessage(ref Message message)
        {
            if (message.Msg is 0x0200 or 0x0202)
            {
                if (message.Msg == 0x0200 && (message.WParam.ToInt64() & 1) == 0) return false;
                // 使用这条鼠标消息自己的有符号坐标，避免快速折返时误用后续事件的位置。
                long coordinates = message.LParam.ToInt64();
                Point pointer = _host.PointToScreen(new Point(unchecked((short)coordinates), unchecked((short)(coordinates >> 16))));
                int dx = pointer.X - _start.X, dy = pointer.Y - _start.Y;
                int left = _initial.Left, right = _initial.Right, top = _initial.Top, bottom = _initial.Bottom;
                if (_edge is HitLeft or HitTopLeft or HitBottomLeft) left = Math.Min(left + dx, right - _host.MinimumSize.Width);
                if (_edge is HitRight or HitTopRight or HitBottomRight) right = Math.Max(right + dx, left + _host.MinimumSize.Width);
                if (_edge is HitTop or HitTopLeft or HitTopRight) top = Math.Min(top + dy, bottom - _host.MinimumSize.Height);
                if (_edge is HitBottom or HitBottomLeft or HitBottomRight) bottom = Math.Max(bottom + dy, top + _host.MinimumSize.Height);
                _host.PresentPreviewSize(Rectangle.FromLTRB(left, top, right, bottom));
                if (message.Msg == 0x0202) Finish(cancel: false);
                else if (!_ended) { _idle.Stop(); _idle.Start(); }
                return true;
            }
            if (message.Msg is 0x0215 or 0x001f)
            {
                Finish(cancel: message.Msg == 0x001f);
            }
            return false;
        }

        /// <summary>先摘除捕获处理器，再归还鼠标，以免释放过程中同步收到的通知重入。取消时还原原尺寸。</summary>
        internal void Finish(bool cancel)
        {
            if (_ended) return;
            Dispose();
            _host._borderDrag = null;
            if (_host.Capture) _host.Capture = false;
            if (cancel) _host.Bounds = _initial;
            _host.RestorePaintedRim();
        }

        /// <summary>释放本次拖动的键盘过滤与空闲计时器，窗口销毁时也可直接清理。</summary>
        public void Dispose()
        {
            _ended = true;
            _idle.Dispose();
            Application.RemoveMessageFilter(this);
        }
    }
}
