/*
 * 文件名称：ActivityLogView.cs
 * 文件职责：显示只读的分级活动记录，保留原生文本选择、键盘复制与滚动能力。
 * 光标约定：RichEdit 获得焦点后关闭插入光标的显示，选择及复制仍由系统处理。
 */
using System.Runtime.InteropServices;

namespace SideStage.App;

internal sealed class ActivityLogView : RichTextBox
{
    /// <summary>建立只读查看器，右键菜单使用与 Ctrl+C 相同的原生复制操作。</summary>
    internal ActivityLogView()
    {
        ReadOnly = true;
        HideSelection = false;
        var menu = new ContextMenuStrip();
        var copy = menu.Items.Add("复制", null, (_, _) => Copy());
        menu.Opening += (_, _) => copy.Enabled = SelectionLength > 0;
        ContextMenuStrip = menu;
    }

    /// <summary>在获取焦点或鼠标完成选区后隐藏插入光标。RichEdit 在单击定位时也会重新显示光标。</summary>
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg is 0x0007 or 0x0202 && Focused) HideCaret(message.HWnd); // WM_SETFOCUS、WM_LBUTTONUP
    }

    /// <summary>随日志查看器释放其专属的菜单资源。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) ContextMenuStrip?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>只改变当前日志窗口原生插入光标的可见性。</summary>
    [DllImport("user32.dll")]
    private static extern bool HideCaret(nint window);
}
