/*
 * 文件名称：DrawerLayoutCheck.cs
 * 文件职责：在当前显示器上验收设置抽屉，核对真实控件的像素边界、文字留白与小窗口滚动范围。
 * 运行方式：给 FrameCheck 传入 --drawer-layout，可再传 PNG 路径保存设置总览和凭据页的截图。
 */
namespace SideStage.App.FrameCheck;

internal static class DrawerLayoutCheck
{
    /// <summary>从首页进入总览，再展开自定义尺寸和域账户。整个流程只操作验收窗口。</summary>
    internal static void Run(ParchmentPreviewForm window, string? screenshot)
    {
        window.Bounds = Screen.FromControl(window).WorkingArea;
        Control[] controls = Children(window).ToArray();
        controls.OfType<Button>().Single(b => b.Text == "高级设置").PerformClick();
        Application.DoEvents();
        var toggle = controls.OfType<SettingsSwitch>().Single(b => b.Text == "保持宽高比");
        var title = controls.OfType<Label>().Single(b => b.Text == "活动日志");
        var info = controls.OfType<ActivityLogInfoButton>().Single();
        var close = controls.OfType<Button>().Single(b => b.AccessibleName == "收起侧栏");
        CheckTab(close);
        Console.WriteLine($"窗口 {window.DeviceDpi} DPI，开关行 {toggle.Height}px，轨道 {toggle.LogicalToDeviceUnits(20)}px，" +
            $"日志标题 {title.Height}px，收起按钮 {close.Height}px。");
        if (screenshot != null) SettingsInteractionCheck.Capture(window, screenshot);
        foreach (var row in controls.Where(c => c.Visible && c is SettingsSwitch or SettingsActionRow or PaperComboBox))
            CheckRow(row, 36);
        CheckRow(info, 27);
        if (title.Height < title.PreferredHeight || title.Bottom + title.Margin.Bottom > title.Parent!.Height)
            throw new InvalidOperationException($"日志标题被裁切：{title.Bounds}，所需高度 {title.PreferredHeight}，标题栏 {title.Parent!.Height}。");

        var selector = controls.OfType<PaperComboBox>().Single(c => c.AccessibleName == "远端分辨率");
        selector.SelectedIndex = selector.Items.Count - 1;
        CheckInputs(window);
        controls.OfType<Button>().Single(b => b.Text == "登录凭据").PerformClick();
        Children(window).OfType<SettingsSwitch>().Single(b => b.Text == "高级选项（域账户）").Checked = true;
        CheckInputs(window);
        CheckTab(close);
        if (screenshot != null) SettingsInteractionCheck.Capture(window, Path.ChangeExtension(screenshot, "credentials.png"));
        controls.OfType<Button>().Single(b => b.AccessibleName == "返回高级设置").PerformClick();
        window.Size = window.MinimumSize;
        Application.DoEvents();
        var page = controls.OfType<PaperScrollPage>().Single(p => p.Visible && p.Contains(toggle));
        var viewport = Children(page).OfType<Panel>().Single(c => c.AutoScroll);
        if (viewport.DisplayRectangle.Height > viewport.ClientSize.Height)
        {
            var last = controls.OfType<Label>().Single(l => l.Visible && l.Text.StartsWith("当前版本"));
            viewport.ScrollControlIntoView(last);
            if (!viewport.RectangleToScreen(viewport.ClientRectangle).Contains(last.RectangleToScreen(last.ClientRectangle)))
                throw new InvalidOperationException("小窗口无法滚动到版本说明。");
        }
        CheckTab(close);
        if (page.Height < toggle.Height)
            throw new InvalidOperationException("小窗口的设置行超出视口。");
        close.PerformClick();
        if (close.Visible || info.Visible) throw new InvalidOperationException("点击把手后，侧栏及把手应一起收起。");
    }

    /// <summary>在工作区坐标内核对把手的真实位置、点击范围和 DPI 比例。</summary>
    internal static void CheckTab(Button tab)
    {
        Control workspace = tab.Parent!;
        Control drawer = workspace.Controls.Cast<Control>().Single(c => Children(c).OfType<PaperLogFrame>().Any());
        if (!tab.Visible || !tab.CanSelect || !tab.TabStop || !workspace.ClientRectangle.Contains(tab.Bounds) ||
            tab.Size != drawer.LogicalToDeviceUnits(new Size(28, 28)) ||
            drawer.Left - tab.Left != Math.Min(drawer.Left, tab.Width / 2) ||
            Math.Abs(tab.Top + tab.Bottom - drawer.Top - drawer.Bottom) > 1 ||
            tab.Right > drawer.Left + drawer.Padding.Left ||
            tab.Region?.IsVisible(0, 0) != false || !tab.Region.IsVisible(tab.Width / 2, tab.Height / 2))
            throw new InvalidOperationException($"把手应贴在侧栏左缘中点：把手 {tab.Bounds}，侧栏 {drawer.Bounds}。");
        CheckArrowCenter(tab);
    }

    /// <summary>只扫描圆面内的深色笔画，在各档缩放下核对双箭头中心与圆心的偏差。</summary>
    private static void CheckArrowCenter(Button tab)
    {
        using Bitmap image = new(tab.Width, tab.Height);
        tab.DrawToBitmap(image, tab.ClientRectangle);
        int left = image.Width, right = -1, top = image.Height, bottom = -1;
        for (int y = image.Height / 4; y < image.Height * 3 / 4; y++)
        for (int x = image.Width / 4; x < image.Width * 3 / 4; x++)
        {
            if (image.GetPixel(x, y).R > 155) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        double horizontalOffset = (left + right - (tab.Width - 1)) / 2d;
        if (right <= left || bottom <= top || Math.Abs(horizontalOffset) > .5 ||
            Math.Abs(top - (tab.Height - bottom - 1)) > 1)
            throw new InvalidOperationException($"双箭头偏移 {horizontalOffset}px，上下留白 {top}/{tab.Height - bottom - 1}。");
    }

    /// <summary>检查整行至少保有设计行高，同时能完整容纳当前字体。</summary>
    private static void CheckRow(Control row, int designHeight)
    {
        int needed = Math.Max(row.LogicalToDeviceUnits(designHeight), (int)Math.Ceiling(row.Font.GetHeight(row.DeviceDpi)));
        if (row.Height < needed)
            throw new InvalidOperationException($"「{row.AccessibleName ?? row.Text}」需要 {needed}px，实际 {row.Height}px。");
    }

    /// <summary>检查新显示的编辑器及其上下留白，涵盖动态建立的凭据页。</summary>
    private static void CheckInputs(Control window)
    {
        Application.DoEvents();
        var rows = Children(window).OfType<SettingsInputRow>().Where(c => c.Visible).ToArray();
        if (rows.Length < 2) throw new InvalidOperationException("预期显示至少两个输入行。");
        foreach (var row in rows)
        {
            CheckRow(row, 39);
            var input = row.Controls[0];
            if (input.Top <= 0 || input.Bottom >= row.Height || input.Right > row.Width)
                throw new InvalidOperationException($"「{row.Text}」输入框超出行边界。");
        }
    }

    /// <summary>枚举真实窗口及延迟创建的子页面控件。</summary>
    private static IEnumerable<Control> Children(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(child => Children(child).Prepend(child));
}
