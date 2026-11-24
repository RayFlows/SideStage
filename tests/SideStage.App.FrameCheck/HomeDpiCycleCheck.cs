/*
 * 文件名称：HomeDpiCycleCheck.cs
 * 文件职责：在同一测试窗口中重放跨屏 DPI 消息，检查来回切换后的纸面、按钮与双向滑块。
 * 测试边界：只向本进程窗口发送 WinForms 支持的带 DPI 参数的消息，保留桌面的实际显示设置。
 */
using System.Runtime.InteropServices;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App.FrameCheck;

internal static class HomeDpiCycleCheck
{
    /// <summary>覆盖真实初始 DPI 和多次往返，并在两个轴都溢出的窗口操作实际滑块。</summary>
    internal static void Run(ParchmentPreviewForm window, string? screenshot)
    {
        var surface = Children(window).OfType<PaperSurface>().Single();
        var viewport = (PaperScrollPage)surface.Parent!.Parent!;
        var table = surface.Controls.OfType<TableLayoutPanel>().Single();
        var button = Children(table).OfType<Button>().Single(c => c.Text == "连接");
        var label = Children(table).OfType<Label>().Single(c => c.Text == "操作系统");
        var work = Screen.FromControl(window).WorkingArea;
        int initialDpi = window.DeviceDpi;
        window.Bounds = work;
        // 先建立侧栏的子句柄，验证隐藏页面也能接收窗口切换通知。
        var settings = Children(table).OfType<Button>().Single(c => c.Text == "高级设置");
        settings.PerformClick();
        var collapse = Children(window).OfType<Button>().Single(c => c.AccessibleName == "收起侧栏");
        collapse.PerformClick();
        foreach (int dpi in new[] { initialDpi, 120, 144, 192, 96, 192, initialDpi })
        {
            if (window.DeviceDpi != dpi) SetDpi(window, dpi);
            window.Bounds = work;
            Application.DoEvents();
            int expectedHeight = (int)Math.Round(38 * dpi / 96d);
            int row = table.GetRowHeights()[1];
            Console.WriteLine($"{dpi} DPI：按钮 {button.Size}，表格行 {row}，内边距 {table.Padding.Left}。");
            if (surface.DeviceDpi != dpi || Math.Abs(button.Height - expectedHeight) > 2 ||
                Math.Abs(button.Width - 138 * dpi / 96d) > 2 || Math.Abs(row - 39 * dpi / 96d) > 2 ||
                Math.Abs(table.Padding.Left - 40 * dpi / 96d) > 1 || label.Height < label.Font.Height)
                throw new InvalidOperationException("首页按钮、表格与纸面边距未恢复到目标 DPI 的比例。");
            if (screenshot != null && dpi == 96) SettingsInteractionCheck.Capture(window, screenshot);
            settings.PerformClick();
            var toggle = Children(window).OfType<SettingsSwitch>().Single(c => c.Text == "保持宽高比");
            if (Math.Abs(toggle.Height - 36 * dpi / 96d) > 2)
                throw new InvalidOperationException($"设置开关在 {dpi} DPI 下的行高错误：{toggle.Height}。");
            DrawerLayoutCheck.CheckTab(collapse);
            collapse.PerformClick();
            if (dpi != 192) continue;
            window.Size = new Size(960, 680);
            Application.DoEvents();
            CheckThumbs(viewport, surface);
            if (screenshot != null) SettingsInteractionCheck.Capture(window, Path.ChangeExtension(screenshot, "compact.png"));
        }
        // 1:1 预览的滚动位置同时决定输入映射。只调用库的几何查询，不发送远端输入。
        ChildSessionPreviewPanel preview = Children(window).OfType<ChildSessionPreviewPanel>().Single();
        window.Size = window.MinimumSize;
        preview.SetSettings(new ChildSessionPreviewSettings(new RemoteDesktopResolution(1920, 1080), PreviewScalingMode.OneToOne, true));
        Panel rdp = (Panel)preview.PreviewControl.Parent!;
        var wrapper = (PaperScrollPage)rdp.Parent!.Parent!;
        CheckThumbs(wrapper, rdp);
        rdp.AutoScrollPosition = new Point(80, 70);
        if (preview.TryMapToRemotePoint(new Point(12, 14)) != new Point(92, 84) ||
            preview.TryMapToRemotePoint(new Point(wrapper.Width - 1, 30)) != null)
            throw new InvalidOperationException("滚动后的预览坐标或滑块命中错误。");
    }

    /// <summary>核对裁切区完全遮蔽两个原生非客户区；拖到两轴末端后再回到开头。</summary>
    private static void CheckThumbs(PaperScrollPage viewport, Panel surface)
    {
        Control clip = surface.Parent!;
        if (!surface.HorizontalScroll.Visible || !surface.VerticalScroll.Visible || surface.ClientSize != clip.Size ||
            clip.Right >= viewport.Width || clip.Bottom >= viewport.Height)
            throw new InvalidOperationException($"系统滚动条未完全裁掉：客户区 {surface.ClientSize}，裁切区 {clip.Size}，页面 {viewport.Size}。");
        surface.AutoScrollPosition = Point.Empty;
        int inset = viewport.LogicalToDeviceUnits(5);
        Drag(viewport, new(inset, viewport.Height - inset), new(clip.Right - 1, viewport.Height - inset));
        Drag(viewport, new(viewport.Width - inset, inset), new(viewport.Width - inset, clip.Bottom - 1));
        var scroll = surface.AutoScrollPosition;
        if (-scroll.X != surface.DisplayRectangle.Width - clip.Width || -scroll.Y != surface.DisplayRectangle.Height - clip.Height)
            throw new InvalidOperationException($"两轴未到末端：{scroll}，内容 {surface.DisplayRectangle}，视口 {clip.Size}。");
        surface.AutoScrollPosition = Point.Empty;
    }

    /// <summary>用客户区的实际像素按下、拖动并释放纸面滑块。</summary>
    private static void Drag(Control owner, Point from, Point to)
    {
        static nint Pack(Point p) => p.X | (p.Y << 16);
        SendMessage(owner.Handle, 0x0201, 1, Pack(from));
        SendMessage(owner.Handle, 0x0200, 1, Pack(to));
        SendMessage(owner.Handle, 0x0202, 0, Pack(to));
    }

    /// <summary>按照 Windows 的顺序，子控件由内向外先接收新 DPI，窗体布局后再由外向内完成通知。</summary>
    private static void SetDpi(Form window, int dpi)
    {
        var children = Children(window).Where(c => c.IsHandleCreated).ToArray();
        foreach (Control c in children.Reverse()) SendMessage(c.Handle, 0x02E2, dpi, 0);
        Rectangle bounds = window.Bounds;
        int[] rect = [bounds.Left, bounds.Top, bounds.Right, bounds.Bottom];
        GCHandle pin = GCHandle.Alloc(rect, GCHandleType.Pinned);
        try { SendMessage(window.Handle, 0x02E0, dpi | (dpi << 16), pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
        foreach (Control c in children.Where(c => c.IsHandleCreated)) SendMessage(c.Handle, 0x02E3, dpi, 0);
        Application.DoEvents();
    }

    /// <summary>枚举托管控件，跳过 RDP 组件在原生实现中自行创建的窗口。</summary>
    private static IEnumerable<Control> Children(Control root) =>
        root.Controls.Cast<Control>().SelectMany(child => Children(child).Prepend(child));

    /// <summary>向当前验收窗口同步分派 DPI 或鼠标消息。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int msg, nint wParam, nint lParam);
}
