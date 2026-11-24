/*
 * 文件名称：ReleaseProgressBar.cs
 * 文件职责：以 SideStage 的黄铜色和浅纸色显示下载进度，不改变 Windows 原生进度条的行为。
 * 交互约定：已知长度显示百分比，未知长度沿用系统的不定进度动画；读屏和高对比度仍由系统处理。
 */
using System.Runtime.InteropServices;

namespace SideStage.App;

internal sealed class ReleaseProgressBar : ProgressBar
{
    /// <summary>窗口可能在确定进度与滚动动画之间重建句柄，每次都只给这个控件应用纸面配色。</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (SystemInformation.HighContrast) return;

        // 原生主题会覆盖进度条的颜色消息。保留原生控件，只停用它自身的主题绘制。
        SetWindowTheme(Handle, string.Empty, string.Empty);
        SendMessage(Handle, 0x0409, 0, ColorTranslator.ToWin32(PaperFieldSurface.Brass)); // PBM_SETBARCOLOR
        SendMessage(Handle, 0x2001, 0, ColorTranslator.ToWin32(PaperFieldSurface.Hover)); // PBM_SETBKCOLOR
    }

    /// <summary>为单一原生窗口选择主题，不修改应用或操作系统的主题偏好。</summary>
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTheme(nint window, string app, string classes);

    /// <summary>把 RGB 颜色传给当前进度条，不替代它的数值、动画或辅助功能接口。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
