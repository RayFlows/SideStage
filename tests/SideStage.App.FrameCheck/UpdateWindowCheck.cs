/*
 * 文件职责：以真实 WinForms 消息循环验收更新窗的布局、正文滚动、换源下载和明确安装。
 * 隔离边界：两个源和安装进程都是内存替身；不会联网、操作会话、生成安装或修改跳过偏好。
 */
using System.Diagnostics;
using System.Drawing.Imaging;
using SideStage.App;

namespace SideStage.App.FrameCheck;

internal static class UpdateWindowCheck
{
    /// <summary>为截图和手动预览提供固定的示例版本；这些版本号不代表已经发布。</summary>
    private static ReleaseOffer Sample() => new(ReleaseVersion.Parse("0.1.0-beta.6")!, new("https://cnb.cool/"),
            "新增应用内更新，可从 CNB 或 GitHub 下载。\n\n改进高缩放下的设置侧栏布局，修复快速切换静音时的重连问题。",
            [new(true, new("https://cnb.cool/")), new(false, new("https://github.com/"))]);

    /// <summary>运行可交互的离线预览；下载进度仅为模拟，收起窗口即结束预览进程。</summary>
    internal static int Preview()
    {
        using var window = new ReleaseUpdateWindow(ReleaseVersion.Parse("0.1.0-beta.5")!, Sample(),
            async (_, progress, token) =>
            {
                for (int i = 0; i <= 100; i += 5) { await Task.Delay(120, token); progress.Report(i); }
                return new("fixture", "fixture");
            },
            _ => { MessageBox.Show("预览完成。未下载或安装文件。", "更新窗口预览"); return Task.CompletedTask; },
            () => { }, CancellationToken.None);
        window.Text = "SideStage 更新 · 离线预览";
        window.VisibleChanged += (_, _) => { if (!window.Visible) Application.ExitThread(); };
        Application.Run(window);
        return 0;
    }

    /// <summary>打开生产窗体，截取初始界面，再通过真实按钮测试用户可见的状态转换。</summary>
    internal static int Run(string image)
    {
        var offer = Sample() with { Notes = UpdateEntryTrial.Notes };
        var download = new TaskCompletionSource<VerifiedUpdate>();
        int installed = 0, skipped = 0;
        using var window = new ReleaseUpdateWindow(ReleaseVersion.Parse("0.1.0-beta.5")!, offer,
            (source, _, _) => source.Domestic ? Task.FromException<VerifiedUpdate>(new IOException("测试源不可用")) : download.Task,
            _ => { installed++; return Task.CompletedTask; }, () => skipped++, CancellationToken.None);
        window.Show();
        Application.DoEvents();
        Control[] controls = Enumerate(window).ToArray();
        var sources = controls.OfType<RadioButton>().ToArray();
        if (!sources.Single(s => s.Text.Contains("CNB")).Checked) throw new InvalidOperationException("未默认选择 CNB。");
        var sections = controls.OfType<Label>().Where(l => l.Text is "更新内容" or "下载来源").ToArray();
        var body = controls.OfType<Label>().Single(l => l.Text == offer.Notes);
        if (sections.Length != 2 || sections.Any(l => !l.Font.Bold || !l.Font.Equals(sections[0].Font) ||
            l.Font.SizeInPoints <= body.Font.SizeInPoints || l.Font.SizeInPoints > body.Font.SizeInPoints + 2))
            throw new InvalidOperationException("两个小标题应使用同一级别、比正文略大的粗体。");
        using (Bitmap shot = new(window.ClientSize.Width, window.ClientSize.Height))
        {
            // 直接绘制真实客户区，避免后台桌面或其他应用被误拍入验收图。
            window.Controls[0].DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size));
            shot.Save(image, ImageFormat.Png);
        }
        VerifySourceAndNotesLayout(window, sources, body, offer.Notes);
        var buttons = controls.OfType<Button>().ToArray();
        Button primary = buttons.Single(b => b.Text == "下载更新");
        Button later = buttons.Single(b => b.Text == "稍后");
        primary.PerformClick();
        WaitFor(() => primary.Text == "重新下载");
        sources.Single(s => s.Text == "GitHub").PerformClick();
        if (sources.Count(s => s.Checked) != 1 || !sources.Single(s => s.Text == "GitHub").Checked)
            throw new InvalidOperationException("点击整个选项应互斥切换下载源。");
        primary.PerformClick();
        later.PerformClick();
        if (window.Visible || installed != 0) throw new InvalidOperationException("后台下载未收起或意外启动了安装。");
        download.SetResult(new("fixture", "fixture"));
        WaitFor(() => primary.Text == "重启并更新");
        window.Show();
        if (installed != 0) throw new InvalidOperationException("完成下载不能自动安装。");
        primary.PerformClick();
        if (installed != 1 || skipped != 0) throw new InvalidOperationException("确认安装没有准确分派一次。");
        Console.WriteLine("更新窗口验收通过：正文随宽度折行、大字号末行可滚达、小标题框外对齐、来源无描边、换源重试和后台下载。");
        return 0;
    }

    /// <summary>在同一屏幕坐标系比较可见边界，确保两个小标题左对齐，正文框独占它们之间的空间。</summary>
    private static void VerifySectionBounds(Label heading, ReleaseNotesFrame frame, Label sourceHeading)
    {
        Rectangle title = heading.RectangleToScreen(heading.ClientRectangle);
        Rectangle notes = frame.RectangleToScreen(frame.ClientRectangle);
        Rectangle sources = sourceHeading.RectangleToScreen(sourceHeading.ClientRectangle);
        if (frame.Contains(heading) || title.Left != sources.Left || title.Bottom >= notes.Top ||
            notes.Height <= 0 || notes.Bottom >= sources.Top)
            throw new InvalidOperationException("「更新内容」应位于正文框上方，与下方的「下载来源」左对齐。");
    }

    /// <summary>用窄窗口、大字体及长正文验证选项排版、无描边按钮和独立滚动的正文视口。</summary>
    private static void VerifySourceAndNotesLayout(Form window, RadioButton[] sources, Label body, string original)
    {
        Size initial = window.ClientSize;
        var options = sources.OrderBy(s => s.Left).ToArray();
        if (options[0].Top != options[1].Top || options[0].Right > options[1].Left)
            throw new InvalidOperationException("常规宽度下来源应并排且不重叠。");
        var controls = Enumerate(window).ToArray();
        var frame = controls.OfType<ReleaseNotesFrame>().Single();
        var heading = controls.OfType<Label>().Single(l => l.Text == "更新内容");
        var sourceHeading = controls.OfType<Label>().Single(l => l.Text == "下载来源");
        VerifySectionBounds(heading, frame, sourceHeading);
        VerifyWrappedBody(body, checkSecondLine: true);
        int wrappedHeight = body.Height;
        window.ClientSize = new Size(initial.Width + TextRenderer.MeasureText(original, body.Font).Width, initial.Height);
        Application.DoEvents();
        VerifyWrappedBody(body);
        if (body.Height >= wrappedHeight) throw new InvalidOperationException("拉宽窗口后，原来的折行没有重新排版。");
        window.ClientSize = initial;
        Application.DoEvents();
        VerifyWrappedBody(body, checkSecondLine: true);
        body.Text = new string('W', 180) + ".exe";
        Application.DoEvents();
        VerifyWrappedBody(body, checkSecondLine: true);
        body.Text = original;
        foreach (var option in options)
        {
            // 直边允许底色与纸面之间的抗锯齿过渡，但不应出现更深或更浅的描边。
            using var face = new Bitmap(option.Width, option.Height);
            option.DrawToBitmap(face, option.ClientRectangle);
            int y = face.Height / 2;
            float edge = face.GetPixel(0, y).GetBrightness();
            float fill = face.GetPixel(option.LogicalToDeviceUnits(4), y).GetBrightness();
            float paper = option.Parent!.BackColor.GetBrightness();
            if (edge < Math.Min(fill, paper) || edge > Math.Max(fill, paper))
                throw new InvalidOperationException($"{option.Text} 应使用无描边的平面底色。");
        }
        var initialFont = window.Font;
        using var larger = new Font(initialFont.FontFamily, initialFont.SizeInPoints * 2);
        window.Font = larger;
        window.Size = new Size(window.MinimumSize.Width, Math.Max(window.Height, window.LogicalToDeviceUnits(530)));
        body.Text = string.Join("\r\n\r\n", Enumerable.Repeat(original, 28));
        Application.DoEvents();
        VerifyWrappedBody(body);
        VerifySectionBounds(heading, frame, sourceHeading);
        if (options[0].Bottom > options[1].Top || options.Any(o => o.Width < o.GetPreferredSize(Size.Empty).Width))
            throw new InvalidOperationException("窄窗口、大字号下应将选项整行排开。");
        var surface = (Panel)body.Parent!;
        if (!surface.VerticalScroll.Visible || surface.HorizontalScroll.Visible)
            throw new InvalidOperationException("长说明应随框宽度换行，只出现纵向滚动条。");
        var before = heading.PointToScreen(Point.Empty);
        surface.AutoScrollPosition = new Point(0, surface.VerticalScroll.Maximum);
        Application.DoEvents();
        if (surface.AutoScrollPosition.Y >= 0 || heading.PointToScreen(Point.Empty) != before ||
            body.Bottom > surface.ClientSize.Height)
            throw new InvalidOperationException("滚到末尾应看见最后一行，小标题仍留在框外。");
        body.Text = original;
        window.Font = initialFont;
        window.ClientSize = initial;
        Application.DoEvents();
        VerifyWrappedBody(body, checkSecondLine: true);
    }

    /// <summary>检查完整内文的可见宽度与换行高度；短段落再验真实像素，防止只有空白第二行。</summary>
    private static void VerifyWrappedBody(Label body, bool checkSecondLine = false)
    {
        var surface = (Panel)body.Parent!;
        int width = Math.Min(surface.ClientSize.Width, surface.Parent!.ClientSize.Width);
        int required = TextRenderer.MeasureText(body.Text, body.Font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        if (body.Width > width || surface.HorizontalScroll.Visible || body.Height < required)
            throw new InvalidOperationException($"正文换行失败：可见宽 {width}，标签 {body.Size}，全文需要 {required} 像素高。");
        if (!checkSecondLine) return;
        using Bitmap rendered = new(body.Width, body.Height);
        body.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));
        for (int y = body.Font.Height + 1; y < rendered.Height; y++)
            for (int x = 0; x < rendered.Width; x++)
                if (Math.Abs(rendered.GetPixel(x, y).GetBrightness() - body.BackColor.GetBrightness()) > .25f) return;
        throw new InvalidOperationException("无显式换行的中文长段落未在第二行画出文字。");
    }

    /// <summary>按实际状态驱动消息循环，超时报告测试失败，避免任意延时造成偶发结果。</summary>
    private static void WaitFor(Func<bool> ready)
    {
        var time = Stopwatch.StartNew();
        while (!ready() && time.Elapsed < TimeSpan.FromSeconds(4)) { Application.DoEvents(); Thread.Sleep(10); }
        if (!ready()) throw new TimeoutException("更新窗口没有到达预期状态。");
    }

    /// <summary>枚举窗体中的真实子控件，不依赖布局深度或屏幕坐标。</summary>
    internal static IEnumerable<Control> Enumerate(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(child => Enumerate(child).Prepend(child));
}
