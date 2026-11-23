/*
 * 文件名称：PreviewResizeSurface.cs
 * 文件职责：在预览窗连续改变大小时，将本窗口的客户区与圆角作为一幅完整画面提交。
 * 像素来源：只请求宿主绘制自己的窗口，临时位图由本次拖动独占，释放后回到原生 RDP 输入。
 */
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SideStage.App;

internal sealed class PreviewResizeSurface : NativeWindow, IDisposable
{
    private ResizeFrameBuffer? _image;
    private bool _visible;

    /// <summary>创建依附宿主、鼠标穿透且无法取得焦点的显示层。位置、尺寸和像素由同一次原生调用提交。</summary>
    internal PreviewResizeSurface(Form host)
    {
        CreateHandle(new CreateParams { Parent = host.Handle, Style = unchecked((int)0x80000000),
            ExStyle = 0x80000 | 0x20 | 0x08000000 | 0x80 });
    }

    /// <summary>读取宿主当前尺寸对应的客户区，补齐不透明像素和外圆弧后交给桌面合成器。</summary>
    internal void Present(Form host)
    {
        Size size = host.ClientSize;
        if (_image == null || _image.Size.Width < size.Width || _image.Size.Height < size.Height)
        {
            // 按实际拖动的最高、最宽值扩容，快速折返时可一直复用同一块位图。
            var capacity = new Size(Math.Max(size.Width + 96, _image?.Size.Width ?? 0),
                Math.Max(size.Height + 96, _image?.Size.Height ?? 0));
            var replacement = new ResizeFrameBuffer(capacity);
            _image?.Dispose();
            _image = replacement;
        }
        if (!PrintWindow(host.Handle, _image.Dc, 3)) throw new Win32Exception();
        _image.ApplyCornerAlpha(host.LogicalToDeviceUnits(BronzeWindowFrame.CornerRadius), size);
        Point position = host.PointToScreen(Point.Empty), source = Point.Empty;
        uint blend = 0x01ff0000;
        if (!UpdateLayeredWindow(Handle, 0, ref position, ref size, _image.Dc, ref source, 0, ref blend, 2))
            throw new Win32Exception();
        if (!_visible) { ShowWindow(Handle, 4); _visible = true; }
    }

    /// <summary>清除本次拖动使用的合成窗口与内存图。</summary>
    public void Dispose()
    {
        if (Handle != 0) DestroyHandle();
        _image?.Dispose();
    }

    /// <summary>请求目标窗口将自己及子窗口绘制到内存设备上下文。</summary>
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    /// <summary>在同一次合成提交中设置画面、大小和屏幕位置。</summary>
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint dc,
        ref Point position, ref Size size, nint sourceDc, ref Point source, uint key, ref uint blend, uint flags);
    /// <summary>显示合成层并保留宿主输入焦点。</summary>
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
}
