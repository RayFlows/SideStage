/*
 * 文件名称：SettingsInteractionCheck.cs
 * 文件职责：验收设置抽屉、日志完整性、齿轮交互、标题栏按钮与关闭确认。
 * 测试边界：用模拟连接状态驱动界面，注销操作由内存委托记录；不接触用户凭据或真实会话。
 */
using System.Runtime.InteropServices;
using System.Text;
using SideStage.Core.Sessions;
using SideStage.Windows.Platform.Windows;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App.FrameCheck;

internal static class SettingsInteractionCheck
{
    /// <summary>通过首页按钮和齿轮分别打开同一个抽屉，并在真实窗口上检查布局与日志文本。</summary>
    internal static int Run(ParchmentPreviewForm window, string? screenshot)
    {
        var paper = Descendants(window).OfType<PaperSurface>().Single();
        var gear = Descendants(window).OfType<SettingsGear>().Single();
        var log = Descendants(window).OfType<RichTextBox>().Single();
        CheckFirstUse(window, screenshot is null ? null : Path.ChangeExtension(screenshot, "home.png"));
        Require(!gear.Visible, "首页使用设置按钮");
        foreach (Button action in Descendants(paper).OfType<Button>())
        {
            string face = SelectedTypeface(action);
            Require(face.Contains("HarmonyOS Sans", StringComparison.OrdinalIgnoreCase), $"{action.Text} 的 GDI 字体为 {face}");
        }
        CheckCredentialPage(window, screenshot);
        CheckClassicPasswordField(window);
        Descendants(paper).OfType<Button>().Single(button => button.Text == "高级设置").PerformClick();
        Require(log.Visible && Descendants(window).OfType<Button>().Single(b => b.Text == "登录凭据").Visible, "侧栏设置");
        var drawer = Descendants(window).OfType<Panel>().Single(panel => panel.Parent?.Contains(paper) == true && panel.Contains(log) && !panel.Contains(paper));
        CheckCollapseHover(window, drawer, screenshot);
        var scaling = Descendants(drawer).OfType<ComboBox>().Single(combo => combo.AccessibleName == "画面缩放");
        var resolution = Descendants(drawer).OfType<ComboBox>().Single(combo => combo.AccessibleName == "远端分辨率");
        CheckLogInfo(window, drawer, screenshot);
        int originalResolution = resolution.SelectedIndex;
        resolution.SelectedIndex = resolution.Items.Count - 1;
        var custom = Descendants(drawer).OfType<TextBox>().Where(field => field.AccessibleName is "宽度" or "高度").ToArray();
        Require(custom.Length == 2 && custom.All(field => field.Visible && field.TextAlign == HorizontalAlignment.Right),
            "自定义宽高在行尾直接编辑");
        var width = custom.Single(field => field.AccessibleName == "宽度");
        var height = custom.Single(field => field.AccessibleName == "高度");
        var settings = Descendants(window).OfType<ChildSessionPreviewPanel>().Single();
        CheckPageScroll(window, Descendants(drawer).OfType<Button>().Single(b => b.Text == "登录凭据"), screenshot, "compact");
        width.Focus();
        width.Text = "1728";
        height.Focus();
        Require(settings.Settings.Resolution.Width == 1728, "离开宽度输入框后应用尺寸");
        height.Text = "972";
        SendMessage(height.Handle, 0x0100, (int)Keys.Enter, 0);
        Require(settings.Settings.Resolution == new RemoteDesktopResolution(1728, 972), "回车提交自定义高度");
        height.Text = "99999";
        width.Focus();
        Require(height.Text == "972" && settings.Settings.Resolution.Height == 972, "超出范围时恢复上次有效值");
        if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, "custom.png"));
        resolution.SelectedIndex = originalResolution;
        Require(custom.All(field => !field.Visible), "切回预设分辨率时完整收起尺寸行");
        if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, "overview.png"));
        CheckHover(scaling);
        CheckHover(Descendants(drawer).OfType<SettingsSwitch>().Single(s => s.Text == "保持宽高比"));
        CheckHover(Descendants(drawer).OfType<Button>().Single(b => b.Text == "登录凭据"));
        int originalIndex = scaling.SelectedIndex;
        nint labelPoint = Pack(new Point(20, scaling.Height / 2));
        SendMessage(scaling.Handle, 0x0201, 1, labelPoint);
        SendMessage(scaling.Handle, 0x0202, 0, labelPoint);
        Require(scaling.DroppedDown, "点击设置名称展开原生选项列表");
        if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, "dropdown.png"));
        SendMessage(scaling.Handle, 0x0100, (int)Keys.Down, 0);
        SendMessage(scaling.Handle, 0x0100, (int)Keys.Enter, 0);
        Require(!scaling.DroppedDown && scaling.SelectedIndex != originalIndex, "方向键选项并回车确认");
        scaling.SelectedIndex = originalIndex;
        var disconnect = Descendants(drawer).OfType<Button>().Single(b => b.Text == "断开连接");
        var logoff = Descendants(drawer).OfType<Button>().Single(b => b.Text == "注销子会话…");
        Require(!disconnect.Visible && !logoff.Visible, "首页设置不显示会话操作");
        Require(paper.Controls[0].Right <= drawer.Left, "侧栏打开时首页状态完整可见");
        string longMessage = string.Concat(Enumerable.Repeat("请核对当前会话状态。", 12));
        window.AppendLog(longMessage, ParchmentPreviewForm.LogLevel.Warning);
        window.AppendLog("测试日志", ParchmentPreviewForm.LogLevel.Error);
        Require(log.WordWrap && log.Text.Contains(longMessage) && ColorAt("INFO") != ColorAt("WARNING") &&
            ColorAt("WARNING") != ColorAt("ERROR") && ColorAt("INFO") != ColorAt("ERROR"), "日志全文及分级颜色");
        int start = log.Text.IndexOf(longMessage, StringComparison.Ordinal);
        Require(log.GetPositionFromCharIndex(start).Y < log.GetPositionFromCharIndex(start + longMessage.Length - 1).Y, "长日志换行");
        log.Select(log.TextLength, 0);
        if (screenshot != null) Capture(window, screenshot);
        CheckLogScroll(log);
        log.Focus();
        Click(log);
        for (int i = 0; i < 5; i++)
        {
            Wait(140);
            ThreadGuiInfo state = new() { Size = (uint)Marshal.SizeOf<ThreadGuiInfo>() };
            if (!GetGUIThreadInfo(GetCurrentThreadId(), ref state))
                throw new InvalidOperationException("无法读取界面验收线程的插入光标状态");
            if (state.Caret == log.Handle && (state.Flags & 1) != 0)
                throw new InvalidOperationException("只读日志仍显示输入光标");
        }
        log.Select(start, longMessage.Length);
        window.AppendLog("选区保留检查");
        Require(log.SelectedText == longMessage && log.ReadOnly, "新增记录时保留可复制的日志选区");
        var preview = Descendants(window).OfType<ChildSessionPreviewPanel>().Single();
        preview.SetPreviewState(RdpPreviewConnectionState.Connecting, false, true, false);
        window.SwitchMainView(RdpPreviewConnectionState.Connecting);
        Require(paper.Visible && !gear.Visible && !preview.PreviewControl.Enabled, "连接期间继续显示纸面首页");
        preview.SetPreviewState(RdpPreviewConnectionState.Connected, false, true, false);
        window.SwitchMainView(RdpPreviewConnectionState.Connected);
        Require(paper.Visible && !gear.Visible, "目标尚未核验时继续显示首页");
        preview.SetPreviewState(RdpPreviewConnectionState.Connected, true, true, true);
        window.SwitchMainView(RdpPreviewConnectionState.Connected);
        Require(gear.Visible && !log.Visible && gear.Right == gear.Parent!.Width, "连接后右上角入口");
        var hub = new Point(gear.Width / 2, gear.Height / 2);
        Require(WindowFromPoint(gear.PointToScreen(hub)) == gear.Handle, "黄铜齿轮轴心可点击");
        if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, "gear.png"));
        Rectangle initialPreview = preview.Bounds;
        Click(gear);
        Require(log.Visible && !gear.Visible && preview.Bounds == initialPreview, "齿轮唤起侧栏且预览尺寸不变");
        window.UpdateSessionCommands(true, false);
        Require(disconnect.Visible && disconnect.Enabled && logoff.Visible && !logoff.Enabled, "目标未核验时只可断开");
        window.UpdateSessionCommands(true, true);
        Require(disconnect.Enabled && logoff.Enabled, "已核验时提供断开和注销");
        if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, "session.png"));
        var tab = Descendants(window).OfType<Button>().Single(b => b.AccessibleName == "收起侧栏");
        Point tabCenter = tab.PointToScreen(new Point(tab.Width / 2, tab.Height / 2));
        Require(WindowFromPoint(tabCenter) == tab.Handle, "预览上可直接点到露出的收起把手");
        Click(tab);
        Require(!log.Visible && !tab.Visible && gear.Visible && WindowFromPoint(tabCenter) != tab.Handle, "关闭侧栏后恢复齿轮并归还把手区域");
        window.SwitchMainView(RdpPreviewConnectionState.Disconnected);
        CheckDrag(window);
        CheckClose();
        return 0;

        // 读取对应标签的富文本颜色，检查三个级别实际呈现的样式。
        Color ColorAt(string label)
        {
            int index = log.Text.IndexOf(label, StringComparison.Ordinal);
            Require(index >= 0, label);
            log.Select(index, label.Length);
            return log.SelectionColor;
        }
    }

    /// <summary>在图标上悬停后移动到气泡，确认链接保持可用，然后用键盘打开同一个目录。</summary>
    private static void CheckLogInfo(Form window, Control drawer, string? screenshot)
    {
        var info = Descendants(drawer).OfType<ActivityLogInfoButton>().Single();
        Point origin = Cursor.Position;
        try
        {
            using Bitmap background = CaptureArea(window.Bounds);
            Cursor.Position = info.PointToScreen(new Point(info.Width / 2, info.Height / 2));
            Wait(SystemInformation.MouseHoverTime + 180);
            Point bubble = info.PointToScreen(new Point(0, info.Height + 12));
            Cursor.Position = bubble;
            Wait(380);
            var hit = Control.FromChildHandle(WindowFromPoint(bubble));
            while (hit != null && hit is not ToolStripDropDown) hit = hit.Parent;
            if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, "help.png"));
            Require(hit is ToolStripDropDown, "悬停后可移入日志气泡");
            var popup = (ToolStripDropDown)hit!;
            using Bitmap foreground = CaptureArea(popup.Bounds);
            int tip = popup.Padding.Top, radius = popup.LogicalToDeviceUnits(8);
            int peak = popup.Width - popup.LogicalToDeviceUnits(14);
            Point offset = new(popup.Left - window.Left, popup.Top - window.Top);
            // 与同一位置弹出前的背景逐像素比较，避免把日志文字和纸纹误认为抗锯齿过渡。
            bool HasTransition(Rectangle area)
            {
                for (int y = area.Top; y < area.Bottom; y++)
                    for (int x = area.Left; x < area.Right; x++)
                    {
                        int before = background.GetPixel(offset.X + x, offset.Y + y).R;
                        int after = foreground.GetPixel(x, y).R;
                        if (after > popup.BackColor.R + 8 && after < before - 8) return true;
                    }
                return false;
            }
            Require(HasTransition(new Rectangle(peak - tip, 0, 2 * tip, tip)) &&
                HasTransition(new Rectangle(0, tip, radius, radius)), "尖角和圆角均有抗锯齿过渡像素");
            var mouseLink = Descendants(popup).OfType<LinkLabel>().Single();
            Cursor.Position = mouseLink.PointToScreen(new Point(mouseLink.Width / 2, mouseLink.Height / 2));
            Wait(60);
            Require(Control.FromChildHandle(WindowFromPoint(Cursor.Position)) == mouseLink, "鼠标能命中日志目录链接");
            Click(mouseLink);
            Require(!popup.Visible, "点击日志目录链接后关闭气泡");
            info.PerformClick();
            var link = Control.FromHandle(GetFocus()) as LinkLabel;
            Require(link != null, "日志目录链接可键盘聚焦");
            // 从键盘消息进入原生链接，与鼠标一样检查目录回调和关闭后的焦点归属。
            PostMessage(link!.Handle, 0x0100, (int)Keys.Enter, 1);
            PostMessage(link.Handle, 0x0101, (int)Keys.Enter, unchecked((int)0xC0000001));
            Wait(80);
            Require(!popup.Visible && info.Focused,
                $"回车打开目录后焦点回到说明按钮（弹层可见 {popup.Visible}，焦点 {Control.FromHandle(GetFocus())?.AccessibleName}）");
        }
        finally { Cursor.Position = origin; }
    }

    /// <summary>获取真实指针命中的窗口及当前键盘焦点，验证气泡的原生输入路径。</summary>
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    /// <summary>读取当前测试线程的焦点句柄。</summary>
    [DllImport("user32.dll")] private static extern nint GetFocus();
    /// <summary>只向验收窗口的消息循环投递按键，保留 WinForms 对菜单和对话键的预处理。</summary>
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
    /// <summary>向系统查询当前线程的插入光标和闪烁状态。</summary>
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref ThreadGuiInfo state);
    /// <summary>只查询本次验收所在的界面线程，避免桌面上其他窗口影响判断。</summary>
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    /// <summary>系统 GUI 线程状态，保留完整原生布局以查询日志查看器的插入光标。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadGuiInfo
    {
        internal uint Size, Flags;
        internal nint Active, Focus, Capture, Menu, Move, Caret;
        internal Rectangle CaretBounds;
    }

    /// <summary>验收首次使用首页。截图取自交互前的真实窗口，随后分别点击配置和启用并核对结果。</summary>
    internal static void CheckFirstUse(ParchmentPreviewForm window, string? screenshot)
    {
        var home = Descendants(window).OfType<PaperSurface>().Single();
        Button Action(string text) => Descendants(home).OfType<Button>().Single(b => b.Text == text);
        var enable = Action("启用子会话");
        var configure = Action("配置登录凭据");
        var connect = Action("连接");
        var os = Descendants(home).OfType<Label>().Single(l => l.AccessibleName == "操作系统状态");
        // 等待独立的后台系统查询，不以测试机是否为家庭版或专业版决定连接按钮状态。
        long deadline = Environment.TickCount64 + 6000;
        while (os.Text == "正在读取…" && Environment.TickCount64 < deadline) Wait(35);
        Require(os.Visible && os.Text.Contains("Windows", StringComparison.OrdinalIgnoreCase) &&
            !os.Text.Contains("未识别", StringComparison.Ordinal), $"首页读取本机操作系统：{os.Text}");
        Require(enable.Visible && enable.Enabled && configure.Visible && configure.Enabled && !connect.Enabled,
            "首次使用同时显示两个可点击的配置入口");
        if (screenshot is not null) Capture(window, screenshot);

        CheckCommandHover(Action("高级设置"), home);
        CheckCommandHover(configure, home);
        configure.PerformClick();
        Require(Descendants(window).OfType<Button>().Any(b => b.Visible && b.Text == "保存凭据"),
            "子会话未启用时也能从首页进入凭据编辑");
        Descendants(window).OfType<Button>().Single(b => b.Visible && b.AccessibleName == "收起侧栏").PerformClick();
        enable.PerformClick();
        Require(!enable.Visible && connect.Enabled && configure.Visible &&
            Descendants(home).OfType<Label>().Single(l => l.AccessibleName == "子会话功能状态").Text == "已启用",
            "系统确认启用后开放连接，可继续配置凭据");
        CheckEditionHint(window, connect, screenshot);
        var connection = Descendants(home).OfType<Label>().Single(l => l.AccessibleName == "RDP 连接状态");
        var existing = new SessionSnapshot(new SessionId(9), SessionConnectionState.Active, true, DateTimeOffset.UtcNow);
        window.UpdateHeaderStatus(RdpPreviewConnectionState.Disconnected, false, existing);
        Require(connection.Text == "未连接", "已存在子会话不代表本窗口已连接");
        window.UpdateHeaderStatus(RdpPreviewConnectionState.Connecting, false, existing);
        Require(connection.Text == "正在连接", "尚在握手的连接不显示已连接");
        window.UpdateHeaderStatus(RdpPreviewConnectionState.Connected, true, existing);
        Require(connection.Text == "已连接 · 会话 9", "已核验连接显示实际会话编号");
        window.UpdateHeaderStatus(RdpPreviewConnectionState.Disconnected, false, null);
    }

    /// <summary>按 SKU 验证版本分类，用真实指针打开家庭版气泡，退出后还原本机系统信息和鼠标位置。</summary>
    private static void CheckEditionHint(ParchmentPreviewForm window, Button connect, string? screenshot)
    {
        Version win = new(10, 0, 26200);
        var indicator = Descendants(window).OfType<EditionIndicator>().Single();
        var real = WindowsEdition.Read();
        Require(new uint[] { 48, 161, 121, 4 }.All(sku => WindowsEdition.Classify(win, 1, sku, false) == EditionMark.ListedHost)
            && new uint[] { 98, 99, 100, 101 }.All(sku => WindowsEdition.Classify(win, 1, sku, false) == EditionMark.Home)
            && WindowsEdition.Classify(win, 3, 7, false) == EditionMark.Unknown
            && WindowsEdition.Classify(win, 3, 7, true) == EditionMark.ListedHost
            && WindowsEdition.Classify(win, 1, 0xFFFF, false) == EditionMark.Unknown, "版本分类区分专业、工作站、教育、企业、家庭及未知版本");
        var origin = Cursor.Position;
        try
        {
            window.DisplayWindowsEdition(new("Windows 11 专业工作站版", EditionMark.ListedHost));
            Require(indicator.Text == "✓" && !indicator.TabStop, "工作站专业版显示勾");
            window.DisplayWindowsEdition(new("Windows 11 家庭版", WindowsEdition.Classify(win, 1, 101, false)));
            Require(indicator.TabStop && indicator.Text == "\uE7BA" && connect.Enabled, "家庭版显示警告但允许连接");
            Cursor.Position = indicator.PointToScreen(new Point(indicator.Width / 2, indicator.Height / 2));
            Wait(SystemInformation.MouseHoverTime + 220);
            var hit = Control.FromChildHandle(WindowFromPoint(indicator.PointToScreen(new Point(25, indicator.Height + 23))));
            while (hit is not null and not PaperHintBubble) hit = hit.Parent;
            Require(hit is PaperHintBubble && Descendants(hit).OfType<Label>().Any(l => l.Text.Contains("本机子会话")),
                "悬停家庭版图标显示兼容性说明");
            if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, "home-edition.png"));
            Cursor.Position = window.PointToScreen(new Point(25, window.Height - 34));
            Wait(550);
            Require(!hit!.Visible, "移出后收起家庭版气泡");
        }
        finally { Cursor.Position = origin; window.DisplayWindowsEdition(real); }
    }

    /// <summary>用测试专属的凭据库走首页与管理入口。只保存和删除测试条目，连接页通过返回退出。</summary>
    private static void CheckCredentialPage(ParchmentPreviewForm window, string? screenshot)
    {
        Button ButtonNamed(string text) => Descendants(window).OfType<Button>().Single(b => b.Visible && (b.Text == text || b.AccessibleName == text));
        TextBox Field(string name) => Descendants(window).OfType<TextBox>().Single(b => b.AccessibleName == name);
        var home = Descendants(window).OfType<PaperSurface>().Single();
        var status = Descendants(home).OfType<Label>().Single(l => l.AccessibleName == "登录凭据状态");
        var rdp = Descendants(home).OfType<Label>().Single(l => l.AccessibleName == "本机远程桌面状态");
        var configure = Descendants(home).OfType<Button>().Single(b => b.Text == "配置登录凭据");
        Require(rdp.Text == "服务和控件就绪" && status.Text == "未保存 · 建议配置", "首页显示本机远程桌面和首次填写状态");
        Descendants(home).OfType<Button>().Single(b => b.Text == "连接").PerformClick();
        Require(Field("账户名").Visible && Field("密码").PasswordChar == '\u00B7', "首次连接使用小圆点遮盖密码");
        Require(Field("账户名").Text == Environment.UserName && Field("密码").TextLength == 0 &&
            !Field("账户名").Focused && !Field("密码").Focused, "首次连接预填当前账户名，等待用户选择输入框");
        var save = Descendants(window).OfType<SettingsSwitch>().Single(s => s.Text == "保存到本机");
        save.Checked = false;
        Field("密码").Text = "frame-check-discard";
        var password = Field("密码");
        password.Focus();
        password.Select(2, 4);
        var eye = ButtonNamed("显示密码");
        Require(password.Right < eye.Left && password.Parent == eye.Parent, "眼睛位于密码输入框右侧");
        Click(eye);
        Require(password.PasswordChar == '\0' && eye.AccessibleName == "隐藏密码" && password.Text == "frame-check-discard" &&
            password.SelectionStart == 2 && password.SelectionLength == 4, "显示密码保留原文和选区");
        eye.PerformClick();
        Require(password.PasswordChar == '\u00B7' && eye.AccessibleName == "显示密码" && password.Text == "frame-check-discard",
            "再次点击恢复小圆点遮盖");
        password.Select(password.TextLength, 0);
        if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, "password-masked.png"));
        eye.PerformClick();
        Click(Descendants(window).OfType<Label>().Single(label => label.Visible && !home.Contains(label) && label.Text == "登录凭据"));
        Require(!password.Focused && password.TextLength > 0, "点击侧栏标题退出编辑并保留草稿");
        password.Focus();
        Click(home);
        Require(!password.Focused, "点击首页纸面退出编辑");
        Click(password);
        Require(password.Focused, "再次点击输入区即可继续编辑");
        Descendants(window).OfType<Button>().Single(b => b.AccessibleName == "返回高级设置").PerformClick();
        ButtonNamed("登录凭据").PerformClick();
        Require(Field("密码").TextLength == 0 && Field("密码").PasswordChar == '\u00B7' && !Field("所属域").Visible,
            "重新进入时清空草稿、遮盖密码并收起域");
        CheckHover(Field("密码").Parent!);
        Field("密码").Text = "frame-check-close";
        ButtonNamed("收起侧栏").PerformClick();
        ButtonNamed("高级设置").PerformClick();
        ButtonNamed("登录凭据").PerformClick();
        Require(Field("密码").TextLength == 0, "收起侧栏后重新进入凭据页，密码草稿已清除");
        if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, "credentials.png"));
        Field("账户名").Text = "PreviewFixture";
        Field("密码").Text = "FrameCheck-only";
        Descendants(window).OfType<CheckBox>().Single(b => b.Text == "高级选项（域账户）").Checked = true;
        Field("所属域").Text = "TEST";
        ButtonNamed("保存凭据").PerformClick();
        Require(status.Text == "已保存到本机" && !configure.Visible, "保存成功后立即更新首页并收起引导");
        ButtonNamed("登录凭据").PerformClick();
        Field("密码").Text = "FrameCheck-replacement";
        ButtonNamed("保存凭据").PerformClick();
        Require(status.Text == "已保存到本机", "覆盖已有凭据后首页仍显示已保存");
        ButtonNamed("登录凭据").PerformClick();
        Require(Field("账户名").Text == Environment.UserName && Field("密码").TextLength == 0 && Field("所属域").TextLength == 0 &&
            !Field("所属域").Visible && !Field("账户名").Focused && !Field("密码").Focused, "已有保存项时只预填当前账户名，等待用户点击");
        ButtonNamed("删除已保存的凭据").PerformClick();
        ButtonNamed("取消").PerformClick();
        Require(ButtonNamed("删除已保存的凭据").Visible && status.Text == "已保存到本机", "取消删除保留原凭据状态");
        ButtonNamed("删除已保存的凭据").PerformClick();
        Descendants(window).OfType<CheckBox>().Single(b => b.Text == "高级选项（域账户）").Checked = true;
        CheckPageScroll(window, ButtonNamed("确认删除"), screenshot, "compact-credentials");
        ButtonNamed("确认删除").PerformClick();
        Require(status.Text == "未保存 · 建议配置" && configure.Visible, "确认删除后首页立即恢复配置入口");
        Require(Field("账户名").TextLength == 0, "侧栏确认删除测试凭据");
        ButtonNamed("收起侧栏").PerformClick();
    }

    /// <summary>经典窗口共用相同的遮盖和眼睛按钮。使用独立的测试目标名，仅在编辑器内切换显示。</summary>
    private static void CheckClassicPasswordField(Form owner)
    {
        var vault = new WindowsChildSessionCredentialStore($"SideStage.FrameCheck.{Guid.NewGuid():N}");
        using CredentialInputDialog dialog = new(vault);
        dialog.Show(owner);
        var eye = Descendants(dialog).OfType<PasswordVisibilityButton>().Single();
        var field = eye.Parent!.Controls.OfType<TextBox>().Single();
        Require(field.PasswordChar == '\u00B7' && eye.Left >= field.Right && eye.Width > 0,
            "经典凭据窗口在小圆点密码框右侧显示眼睛");
        field.Text = "classic-fixture";
        eye.PerformClick();
        Require(field.PasswordChar == '\0' && field.Text == "classic-fixture", "经典窗口可查看本次输入");
        field.Clear();
        Require(field.PasswordChar == '\u00B7' && eye.AccessibleName == "显示密码", "清空后再次输入仍默认遮盖");
        dialog.Close();
    }

    /// <summary>在最小窗口操作真实页面，核对系统滚动条裁切、滚轮、拖动、键盘导航和固定日志的位置。</summary>
    private static void CheckPageScroll(Form window, Button last, string? screenshot, string captureName)
    {
        Size original = window.Size;
        Control frame = Descendants(window).OfType<PaperLogFrame>().Single();
        try
        {
            window.Size = window.MinimumSize;
            var page = Descendants(window).OfType<PaperScrollPage>().Single(p => p.Visible && p.Contains(last));
            var viewport = Descendants(page).OfType<Panel>().Single(p => p.AutoScroll);
            Control clip = viewport.Parent!;
            viewport.AutoScrollPosition = Point.Empty;
            Wait(60);
            Rectangle fixedLog = frame.RectangleToScreen(frame.ClientRectangle);
            Require(viewport.VerticalScroll.Visible && viewport.ClientSize.Width == clip.Width &&
                clip.Width < page.Width && viewport.Width > clip.Width, "最小窗口裁掉系统滚动条并保留贴边滑块");
            SendMessage(viewport.Handle, 0x020A, -120 << 16, Pack(viewport.PointToScreen(new Point(20, 20))));
            Require(viewport.AutoScrollPosition.Y < 0, "页面内容响应滚轮");
            viewport.AutoScrollPosition = Point.Empty;
            int x = page.Width - page.LogicalToDeviceUnits(5);
            nint top = Pack(new Point(x, page.LogicalToDeviceUnits(6)));
            nint bottom = Pack(new Point(x, page.Height - 1));
            SendMessage(page.Handle, 0x0201, 1, top);
            SendMessage(page.Handle, 0x0200, 1, bottom);
            SendMessage(page.Handle, 0x0202, 0, bottom);
            Rectangle visible = clip.RectangleToScreen(clip.ClientRectangle);
            // 末端可能是版本说明；可聚焦的最后一个按钮在下方的键盘步骤另行验证。
            Require(-viewport.AutoScrollPosition.Y == viewport.DisplayRectangle.Height - viewport.ClientSize.Height,
                "拖动设置滑块可以到达内容末端");
            Require(!page.Capture && frame.RectangleToScreen(frame.ClientRectangle) == fixedLog, "松开滑块后释放捕获且日志位置固定");
            if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, $"{captureName}.png"));
            // 由首页第一项依 Tab 顺序前进，屏外的操作获得焦点时必须被滚入视口。
            Control? focused = null;
            viewport.SelectNextControl(null, true, true, true, false);
            for (int i = 0; i < 40 && !last.Focused; i++)
            {
                focused = Control.FromHandle(GetFocus());
                if (!viewport.SelectNextControl(focused, true, true, true, false)) break;
            }
            Require(last.Focused && visible.Contains(last.RectangleToScreen(last.ClientRectangle)),
                $"键盘可进入末端操作并显示整行（当前焦点 {Control.FromHandle(GetFocus())?.AccessibleName}，末项 {last.RectangleToScreen(last.ClientRectangle)}，视口 {visible}）");
            // 放大时日志框也会增长，因此按日志完整高度预留空间，再验证滚动条退出。
            window.Height += viewport.DisplayRectangle.Height - viewport.ClientSize.Height + window.LogicalToDeviceUnits(136);
            Wait(60);
            bool overflow = viewport.Controls[0].Height > viewport.ClientSize.Height;
            Require(viewport.VerticalScroll.Visible == overflow &&
                clip.Width == page.ClientSize.Width - (overflow ? page.LogicalToDeviceUnits(12) : 0),
                $"放大后按实际内容分配滑块位置（内容 {viewport.Controls[0].Height}，视口 {viewport.ClientSize.Height}）");
        }
        finally { window.Size = original; }
    }

    /// <summary>在设置侧栏展开时点击真实标题栏按钮，检查还原后不保留高亮，重新悬停仍有反馈。</summary>
    internal static int CheckCaptionButtons(Form window)
    {
        Button minimize = window.Controls.OfType<Button>().Single(b => b.AccessibleName == "最小化");
        Button maximize = window.Controls.OfType<Button>().Single(b => b.AccessibleName == "最大化");
        Point original = Cursor.Position;
        Point away = window.PointToScreen(new Point(window.ClientSize.Width / 2, window.ClientSize.Height / 2));
        try
        {
            Descendants(window).OfType<Button>().Single(b => b.Text == "高级设置").PerformClick();
            var content = Descendants(window).OfType<Button>().Single(b => b.Text == "登录凭据");
            Require(content.Focus(), "先将键盘焦点放在设置侧栏");
            HoverCaption(minimize);
            Click(minimize);
            Wait(400);
            Require(window.WindowState == FormWindowState.Minimized, "点击最小化进入任务栏");
            Cursor.Position = away;
            // 发送系统还原命令，不点击内容区，否则会意外改变焦点而掩盖残留状态。
            SendMessage(window.Handle, 0x0112, 0xF120, 0); // WM_SYSCOMMAND / SC_RESTORE
            window.Activate();
            Wait(400);
            Require(window.WindowState == FormWindowState.Normal, "从最小化还原到普通窗口");
            bool passed = CheckCaptionFace(minimize, false, "最小化后还原");
            Require(content.Focused, "从任务栏还原后保留设置项焦点");

            foreach (FormWindowState target in new[] { FormWindowState.Maximized, FormWindowState.Normal })
            {
                HoverCaption(maximize);
                Click(maximize);
                Wait(400);
                Require(window.WindowState == target, $"点击标题键切换到 {target}");
                // 通常窗口换位已使按钮离开指针；小屏上两种窗口宽度可能相同，此时显式移开。
                if (maximize.RectangleToScreen(maximize.ClientRectangle).Contains(Cursor.Position))
                {
                    Cursor.Position = away;
                    Wait(100);
                }
                passed &= CheckCaptionFace(maximize, false, target == FormWindowState.Maximized ? "最大化后" : "还原大小后");
                Require(content.Focused, "切换窗口大小不抢走设置项焦点");
            }
            HoverCaption(window.Controls.OfType<Button>().Single(b => b.AccessibleName == "关闭"));
            maximize.AccessibilityObject.DoDefaultAction();
            Require(window.WindowState == FormWindowState.Maximized, "辅助技术仍可调用窗口命令");
            maximize.PerformClick();
            Require(window.WindowState == FormWindowState.Normal, "程序仍可调用还原命令");
            return passed ? 0 : 1;
        }
        finally { Cursor.Position = original; }
    }

    /// <summary>移入按钮并确认真实悬停色；防止通过完全禁用高亮使回归用例误通过。</summary>
    private static void HoverCaption(Button button)
    {
        Cursor.Position = button.PointToScreen(new Point(button.Width / 2, button.Height / 2));
        Wait(100);
        Require(CheckCaptionFace(button, true, $"{button.AccessibleName} 悬停"), "标题键显示真实悬停反馈");
    }

    /// <summary>在按钮上缘内侧避开字形和圆角取样，并确认采样点属于本次验收的窗口。</summary>
    private static bool CheckCaptionFace(Button button, bool hovered, string stage)
    {
        Point sample = button.PointToScreen(new Point(button.Width / 2, button.LogicalToDeviceUnits(4)));
        Require(WindowFromPoint(sample) == button.Handle, "标题键采样点未被遮挡");
        using Bitmap pixel = CaptureArea(new Rectangle(sample, new Size(1, 1)));
        Color actual = pixel.GetPixel(0, 0);
        Color expected = hovered ? button.FlatAppearance.MouseOverBackColor : button.BackColor;
        Console.WriteLine($"{stage}：实际 {actual.Name}，预期 {expected.Name}，按钮有焦点={button.Focused}");
        return actual.ToArgb() == expected.ToArgb();
    }

    /// <summary>在首页浅色按钮上比较鼠标进出前后的实色像素，避开文字、光标和圆角。</summary>
    private static void CheckCommandHover(Button button, Control home)
    {
        Point original = Cursor.Position;
        Point paper = home.PointToScreen(new Point(12, 12));
        Point sample = button.PointToScreen(new Point(button.Width / 2, button.LogicalToDeviceUnits(3)));
        Rectangle pixel = new(sample, new Size(1, 1));
        try
        {
            Cursor.Position = paper;
            Wait(80);
            using Bitmap idle = CaptureArea(pixel);
            Cursor.Position = button.PointToScreen(new Point(button.Width / 2, button.Height / 2));
            Wait(80);
            using Bitmap hovered = CaptureArea(pixel);
            Cursor.Position = paper;
            Wait(80);
            using Bitmap restored = CaptureArea(pixel);
            Color before = idle.GetPixel(0, 0), after = hovered.GetPixel(0, 0);
            Require(before.G - after.G >= 16 && before == restored.GetPixel(0, 0),
                $"{button.Text} 悬停时按钮加深，移出后恢复原色");
        }
        finally { Cursor.Position = original; }
    }

    /// <summary>分别经过分隔线两边的半圆，核对圆面反馈、圆外透底和移出后的提示状态。</summary>
    private static void CheckCollapseHover(Form window, Control drawer, string? screenshot)
    {
        var tab = Descendants(window).OfType<DrawerCollapseTab>().Single();
        Point original = Cursor.Position;
        Rectangle bounds = tab.RectangleToScreen(tab.ClientRectangle);
        int center = drawer.PointToScreen(Point.Empty).X - bounds.Left;
        int sampleY = tab.Height / 4;
        Point outside = new(tab.Width - 2, 1);
        Point away = new(bounds.Left - tab.LogicalToDeviceUnits(15), bounds.Top);
        try
        {
            Cursor.Position = away;
            Wait(80);
            using Bitmap idle = CaptureArea(bounds);
            Color idleFace = idle.GetPixel(center, sampleY);
            Require(idleFace.G - drawer.BackColor.G >= 10 && idleFace.G - tab.FlatAppearance.MouseOverBackColor.G >= 10 &&
                idle.GetPixel(outside.X, outside.Y).ToArgb() == drawer.BackColor.ToArgb(), "常态圆钮与侧栏纸色可区分，圆外显示侧栏原色");
            if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, "collapse-idle.png"));
            foreach (int x in new[] { tab.Width / 4, tab.Width * 3 / 4 })
            {
                Cursor.Position = new Point(bounds.Left + x, bounds.Top + bounds.Height / 2);
                Wait(80);
                using Bitmap hovered = CaptureArea(bounds);
                Require(hovered.GetPixel(center - 2, sampleY).ToArgb() == tab.FlatAppearance.MouseOverBackColor.ToArgb() &&
                    hovered.GetPixel(center + 2, sampleY).ToArgb() == tab.FlatAppearance.MouseOverBackColor.ToArgb() &&
                    hovered.GetPixel(outside.X, outside.Y) == idle.GetPixel(outside.X, outside.Y) &&
                    WindowFromPoint(tab.PointToScreen(outside)) != tab.Handle, "两侧半圆同色高亮，圆外保留原色与原命中窗口");
            }
            Wait(SystemInformation.MouseHoverTime + 200);
            var popup = Control.FromChildHandle(WindowFromPoint(new Point(bounds.Left, bounds.Bottom + tab.LogicalToDeviceUnits(16))));
            while (popup != null && popup is not PaperHintBubble) popup = popup.Parent;
            Require(popup is PaperHintBubble { Visible: true }, "停留后把手左下方显示纸面提示");
            if (screenshot != null) Capture(window, Path.ChangeExtension(screenshot, "collapse-hover.png"));
            Cursor.Position = away;
            Wait(80);
            using Bitmap restored = CaptureArea(bounds);
            Require(!popup!.Visible && restored.GetPixel(center, sampleY) == idleFace,
                "移出后提示收起，圆钮恢复浅纸色");
        }
        finally { Cursor.Position = original; }
    }

    /// <summary>移动真实指针经过名称、空白和值，读取整行两端的合成像素。退出后复原指针位置。</summary>
    private static void CheckHover(Control row)
    {
        Point original = Cursor.Position;
        Rectangle bounds = row.RectangleToScreen(row.ClientRectangle);
        using Bitmap strip = new(bounds.Width, 1);
        using Graphics graphics = Graphics.FromImage(strip);
        try
        {
            foreach (int x in new[] { 18, bounds.Width / 2, bounds.Width - 20 })
            {
                Cursor.Position = new Point(bounds.Left + x, bounds.Top + bounds.Height / 2);
                Wait(80);
                DwmFlush();
                graphics.CopyFromScreen(bounds.Left, Cursor.Position.Y, 0, 0, strip.Size);
                Require(new[] { 3, bounds.Width - 4 }.All(edge => strip.GetPixel(edge, 0).ToArgb() ==
                    PaperFieldSurface.Hover.ToArgb()), $"{row.AccessibleName ?? row.Text} 悬停 {x}，行首和行尾同色");
                if (row.Top > 0 && row.Parent is SettingsList list && row.Bottom < list.Height)
                {
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, strip.Size);
                    Require(strip.GetPixel(bounds.Width / 2, 0).ToArgb() == PaperFieldSurface.Hover.ToArgb(), "悬停覆盖上分隔线");
                    graphics.CopyFromScreen(bounds.Left, bounds.Bottom, 0, 0, strip.Size);
                    Require(strip.GetPixel(bounds.Width / 2, 0).ToArgb() != Color.FromArgb(226, 216, 197).ToArgb(), "悬停隐藏下分隔线");
                }
            }
        }
        finally { Cursor.Position = original; Wait(40); }
    }

    /// <summary>通过日志右缘的鼠标消息拖动滑块，检查原生日志的首行、滚轮和选区。</summary>
    private static void CheckLogScroll(RichTextBox log)
    {
        Control frame = log.Parent!;
        int x = frame.Width - frame.LogicalToDeviceUnits(5);
        const int firstLine = 0x00CE;
        int selection = log.SelectionStart;
        int last = (int)SendMessage(log.Handle, firstLine, 0, 0);
        Require(last > 0, "长日志已滚到末页");
        Point bottom = new(x, frame.Height - frame.Padding.Bottom - 2), top = new(x, frame.Padding.Top);
        SendMessage(frame.Handle, 0x0201, 1, Pack(bottom));
        SendMessage(frame.Handle, 0x0200, 1, Pack(top));
        SendMessage(frame.Handle, 0x0202, 0, Pack(top));
        Require((int)SendMessage(log.Handle, firstLine, 0, 0) == 0 && log.SelectionStart == selection, "贴边滑块可拖至首行并保留选区");
        SendMessage(log.Handle, 0x020A, -120 << 16, Pack(log.PointToScreen(new Point(20, 20))));
        Require((int)SendMessage(log.Handle, firstLine, 0, 0) > 0, "正文响应滚轮");
    }

    /// <summary>读取原生按钮实际使用的字族名。检查私有字体在 GDI 中也已注册，而非只在 Font 对象上同名。</summary>
    private static string SelectedTypeface(Control control)
    {
        using Graphics graphics = control.CreateGraphics();
        nint dc = graphics.GetHdc();
        // 自绘 Flat 按钮用 TextRenderer 绘字，未必持有 WM_GETFONT 句柄；用它实际的 Font 建立等价的 GDI 字体。
        nint handle = SendMessage(control.Handle, 0x0031, 0, 0);
        bool ownsFont = handle == 0;
        if (ownsFont) handle = control.Font.ToHfont();
        nint original = SelectObject(dc, handle);
        try
        {
            StringBuilder name = new(64);
            GetTextFace(dc, name.Capacity, name);
            return name.ToString();
        }
        finally
        {
            SelectObject(dc, original);
            if (ownsFont) DeleteObject(handle);
            graphics.ReleaseHdc(dc);
        }
    }

    /// <summary>截取真实桌面合成帧，保留内框、字体与控件显示位置供核对。</summary>
    internal static void Capture(Form window, string path)
    {
        window.Refresh();
        using Bitmap image = CaptureArea(window.Bounds);
        image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>读取指定屏幕区域的最终颜色，包含纸面及桌面合成后的透明边缘。</summary>
    private static Bitmap CaptureArea(Rectangle bounds)
    {
        DwmFlush();
        Bitmap image = new(bounds.Width, bounds.Height);
        using Graphics graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, image.Size);
        return image;
    }

    /// <summary>等候桌面提交刚刚刷新的窗口。</summary>
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    /// <summary>在测试 DC 中临时使用控件正在使用的原生字体。</summary>
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    /// <summary>释放测试为自绘控件临时生成的 GDI 字体。</summary>
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    /// <summary>从 GDI 读取实际选中的字族，而非查询应用请求的名称。</summary>
    [DllImport("gdi32.dll", EntryPoint = "GetTextFaceW", CharSet = CharSet.Unicode)]
    private static extern int GetTextFace(nint dc, int capacity, StringBuilder name);

    /// <summary>使用独立工作区操作齿轮，检查四边吸附、拖动与点击区分、五秒收起和悬停展开。</summary>
    private static void CheckDrag(Form owner)
    {
        using Form fixture = new() { StartPosition = FormStartPosition.Manual, Bounds = owner.Bounds,
            ShowInTaskbar = false, TopMost = true };
        using SettingsGear pin = new();
        using Panel backdrop = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
        fixture.Controls.Add(backdrop);
        fixture.Controls.Add(pin);
        pin.BringToFront();
        fixture.Show(owner);
        pin.Reposition();
        CheckGearEdges(pin, backdrop);
        int clicks = 0;
        pin.Click += (_, _) => clicks++;
        Point middle = new(pin.Width / 2, pin.Height / 2);
        Size area = fixture.ClientSize;
        foreach (Point center in new Point[] { new(2, area.Height / 2), new(area.Width / 2, 2),
            new(area.Width / 2, area.Height - 2), new(area.Width - 2, area.Height / 3) })
        {
            SendMessage(pin.Handle, 0x0201, 1, Pack(middle));
            SendMessage(pin.Handle, 0x0200, 1, Pack(new Point(center.X - pin.Left, center.Y - pin.Top)));
            SendMessage(pin.Handle, 0x0202, 0, Pack(middle));
            Wait(350);
            bool docked = center.X == 2 ? pin.Left == 0 : center.Y == 2 ? pin.Top == 0 :
                center.Y == area.Height - 2 ? pin.Bottom == area.Height : pin.Right == area.Width;
            Require(docked && clicks == 0, "拖动吸附");
        }
        Wait(5350);
        Require(pin.Right > area.Width + pin.Width / 2, "五秒后收起");
        SendMessage(pin.Handle, 0x0200, 0, Pack(middle));
        Wait(380);
        Require(pin.Right == area.Width, "悬停展开");
        Click(pin);
        Require(clicks == 1, "单击只触发一次");
    }

    /// <summary>在齿轮下方切换原生子窗口的背景，分别验证齿尖和轮辐孔的半透明像素随底图改变。</summary>
    private static void CheckGearEdges(SettingsGear gear, Panel backdrop)
    {
        Rectangle bounds = gear.RectangleToScreen(gear.ClientRectangle);
        Wait(350);
        using Bitmap dark = CaptureArea(bounds);
        backdrop.BackColor = Color.White;
        backdrop.Refresh();
        Wait(80);
        using Bitmap light = CaptureArea(bounds);
        int hubX = gear.Width / 2, hubY = gear.Height / 2;
        Require(Math.Abs(light.GetPixel(hubX, hubY).R - dark.GetPixel(hubX, hubY).R) < 8 &&
            dark.GetPixel(1, 1).R < 8 && light.GetPixel(1, 1).R > 247, "轴心保持实色且透明区域完整透出底图");
        int outer = 0, inner = 0;
        for (int y = 0; y < bounds.Height; y++)
        {
            for (int x = 0; x < bounds.Width; x++)
            {
                int delta = light.GetPixel(x, y).R - dark.GetPixel(x, y).R;
                if (delta is < 10 or > 245) continue;
                double dx = (x + 0.5) / bounds.Width - 0.5, dy = (y + 0.5) / bounds.Height - 0.5;
                if (dx * dx + dy * dy > 0.36 * 0.36) outer++;
                else inner++;
            }
        }
        Require(outer > 20 && inner > 20, $"齿轮随底图混合的过渡像素：外缘 {outer}，内孔 {inner}");
        Point hole = gear.PointToScreen(new Point(gear.Width / 2, gear.Height * 28 / 100));
        Require(WindowFromPoint(hole) == backdrop.Handle, "轮辐镂空处可点击底图");
    }

    /// <summary>确认默认取消、目标变更、仅断开和成功注销都只触发对应动作。</summary>
    private static void CheckClose()
    {
        SessionId id = new(7);
        var active = (true, (SessionId?)id);
        List<SessionId> calls = [];
        Require(ParchmentPreviewForm.ConfirmPreviewClose(() => (false, null), _ => throw new InvalidOperationException(), calls.Add), "首页直接关闭");
        Require(!ParchmentPreviewForm.ConfirmPreviewClose(() => active, _ => false, calls.Add), "取消保留连接");
        Require(ParchmentPreviewForm.ConfirmPreviewClose(() => (true, null), x => x == null, calls.Add), "保留未核验目标");
        int reads = 0;
        try
        {
            ParchmentPreviewForm.ConfirmPreviewClose(() => ++reads == 1 ? active : (true, new SessionId(8)),
                _ => true, calls.Add);
            throw new ApplicationException("目标变化时仍然关闭");
        }
        catch (InvalidOperationException) { }
        Require(calls.Count == 0 && ParchmentPreviewForm.ConfirmPreviewClose(() => active, target => target == id, calls.Add)
            && calls.SequenceEqual([id]), "只注销经两次核验的目标");
    }

    /// <summary>按下并释放图片中心，走真实鼠标消息路径。</summary>
    private static void Click(Control pin)
    {
        nint p = Pack(new Point(pin.Width / 2, pin.Height / 2));
        SendMessage(pin.Handle, 0x0201, 1, p);
        SendMessage(pin.Handle, 0x0202, 0, p);
    }

    /// <summary>枚举页面控件以检查用户真正可用的入口。</summary>
    private static IEnumerable<Control> Descendants(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(child => Descendants(child).Prepend(child));

    /// <summary>把客户区坐标编码进 Windows 鼠标消息的两个有符号短整型字段。</summary>
    private static nint Pack(Point p) => (ushort)p.X | ((nint)(ushort)p.Y << 16);

    /// <summary>在验收线程中正常处理鼠标及动画计时器。</summary>
    private static void Wait(int milliseconds)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(8); }
    }

    /// <summary>带上操作名称，便于定位实际失败的交互。</summary>
    private static void Require(bool condition, string stage)
    {
        if (!condition) throw new InvalidOperationException($"设置交互验收失败：{stage}");
        Console.WriteLine($"通过：{stage}");
    }

    /// <summary>仅向验收宿主自己的控件发送点击和拖动消息。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
