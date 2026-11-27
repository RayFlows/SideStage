/*
 * 文件职责：在真实的本机子会话中对比自适应和 1:1 的屏幕像素，验证 RDP 图像随窗口双向缩放。
 * 验收条件：屏幕大于请求的 1080p 桌面，子会话有可辨认的静态内容。图像仅用于内存采样。
 */
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App.FrameCheck;

internal static class AdaptiveSizeCheck
{
    /// <summary>以小窗口的远端图像为参照，分别比较最大化后的填满画面和居中原尺寸画面。</summary>
    internal static int Run(Form window, ChildSessionPreviewPanel panel)
    {
        var settings = panel.Settings;
        Size remote = settings.Resolution.ToSize();
        using var reference = Capture(panel.PreviewControl);
        window.WindowState = FormWindowState.Maximized;
        using var adaptive = Capture(panel.PreviewControl);
        if (adaptive.Width < remote.Width * 1.08 || adaptive.Height < remote.Height * 1.08 ||
            reference.Width >= remote.Width || reference.Height >= remote.Height)
            throw new InvalidOperationException($"验收需要小窗及大于 {remote} 的最大化画面。实际为 {reference.Size}、{adaptive.Size}。");

        Rectangle fitted = new(Point.Empty, adaptive.Size);
        Rectangle unscaled = new((adaptive.Width - remote.Width) / 2, (adaptive.Height - remote.Height) / 2,
            remote.Width, remote.Height);
        double fitError = Difference(reference, adaptive, fitted);
        double nativeError = Difference(reference, adaptive, unscaled);
        Console.WriteLine($"远端 {remote}，小窗 {reference.Size}，最大化 {adaptive.Size}。填满模型色差 {fitError:F1}，原尺寸居中模型色差 {nativeError:F1}。");
        if (fitError + 2 >= nativeError) return 1;

        // 同一次连接内切换到 1:1，再切回自适应，防止启用放大改变原像素模式或只在首次连接生效。
        panel.SetSettings(new(settings.Resolution, PreviewScalingMode.OneToOne, settings.PreserveAspectRatio));
        using var native = Capture(panel.PreviewControl);
        if (native.Size != remote || Difference(reference, native, new(Point.Empty, native.Size)) > fitError + 12) return 1;
        panel.SetSettings(settings);
        using var restored = Capture(panel.PreviewControl);
        return restored.Size == fitted.Size && Difference(reference, restored, fitted) + 2 < nativeError ? 0 : 1;
    }

    /// <summary>等待远端重新绘制并采集 ActiveX 的实际屏幕内容，避免把外层控件尺寸误当作远端画面尺寸。</summary>
    private static Bitmap Capture(Control control)
    {
        long deadline = Environment.TickCount64 + 1700;
        while (Environment.TickCount64 < deadline) { Application.DoEvents(); Thread.Sleep(15); }
        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
        var image = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return image;
    }

    /// <summary>将两次采样按指定目标矩形对齐，计算分布在画面内部的 RGB 平均色差。</summary>
    private static double Difference(Bitmap reference, Bitmap actual, Rectangle target)
    {
        long sum = 0;
        int count = 0;
        for (int y = 1; y < 30; y++)
            for (int x = 1; x < 50; x++)
            {
                var a = reference.GetPixel(reference.Width * x / 50, reference.Height * y / 30);
                var b = actual.GetPixel(target.X + target.Width * x / 50, target.Y + target.Height * y / 30);
                sum += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                count++;
            }
        return (double)sum / (3 * count);
    }
}
