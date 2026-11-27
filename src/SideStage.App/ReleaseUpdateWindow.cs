/*
 * 文件职责：以 SideStage 纸面配色显示发行说明、下载源、进度和安装操作。
 * 交互规则：非模态窗口；稍后或关闭只收起窗口。下载可取消或切到后台，不因下载成功自动安装。
 * 生命周期：主窗口持有同一版本的窗口，重新打开保留进度。只有独立更新器准备好后才允许退出。
 */
using System.Net.Http;
using System.Security.Cryptography;

namespace SideStage.App;

internal sealed class ReleaseUpdateWindow : Form
{
    private readonly ReleaseOffer _offer;
    private readonly Func<UpdateSource, IProgress<int>, CancellationToken, Task<VerifiedUpdate>> _download;
    private readonly Func<VerifiedUpdate, Task> _install;
    private readonly Action? _readyToInstall;
    private readonly CancellationToken _lifetime;
    private readonly FlowLayoutPanel _sources = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true,
        Margin = new Padding(2, 4, 2, 0) };
    private readonly ReleaseProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 8, Visible = false,
        AccessibleName = "更新下载进度" };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false };
    private readonly PaperCommandButton _primary = PaperCommandButton.DialogAction("下载更新", true),
        _later = PaperCommandButton.DialogAction("稍后"), _skip = PaperCommandButton.DialogAction("跳过此版本"),
        _cancel = PaperCommandButton.DialogAction("取消下载");
    private readonly Font _body = TheatreTypography.Create(10), _heading = TheatreTypography.Create(16, FontStyle.Bold);
    private readonly Font _section = TheatreTypography.Create(11, FontStyle.Bold), _hint = TheatreTypography.Create(9);
    private CancellationTokenSource? _transfer;
    private VerifiedUpdate? _verified;
    private bool _installing;

    /// <summary>用户已经确认下载或安装，此时修改以后接收的更新渠道应保留这次操作。</summary>
    internal bool HasAcceptedUpdate => _transfer != null || _verified != null || _installing;

    /// <summary>仅渲染已解析的纯文本。下载、交接及跳过行为由主窗口注入，可离线验收，不接触真实会话。</summary>
    internal ReleaseUpdateWindow(ReleaseVersion current, ReleaseOffer offer,
        Func<UpdateSource, IProgress<int>, CancellationToken, Task<VerifiedUpdate>> download,
        Func<VerifiedUpdate, Task> install, Action skip, CancellationToken lifetime, Action? readyToInstall = null)
    {
        (_offer, _download, _install, _lifetime, _readyToInstall) = (offer, download, install, lifetime, readyToInstall);
        Program.ApplyIdentity(this);
        Text = "SideStage 更新";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        Font = _body;
        ForeColor = PaperFieldSurface.Ink;
        BackColor = PaperFieldSurface.Paper;
        ClientSize = new Size(546, 428);
        MinimumSize = new Size(430, 340);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 14),
            ColumnCount = 1, RowCount = 9 };
        grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        // 只有正文框占用剩余高度；两个小标题与下载操作都留在滚动区域之外。
        for (int i = 0; i < grid.RowCount; i++)
            grid.RowStyles.Add(i == 3 ? new(SizeType.Percent, 100) : new(SizeType.AutoSize));
        grid.Controls.Add(Caption("发现 SideStage 新版本", _heading), 0, 0);
        grid.Controls.Add(Caption($"当前 {current.Text}   →   {offer.Version.Text}", _body), 0, 1);
        grid.Controls.Add(Caption("更新内容", _section, 12), 0, 2);
        grid.Controls.Add(new ReleaseNotesFrame(offer.Notes) { Margin = new Padding(2, 4, 2, 0) }, 0, 3);
        grid.Controls.Add(Caption("下载来源", _section, 12), 0, 4);
        foreach (var source in offer.Sources)
            _sources.Controls.Add(new ReleaseSourceOption(source, _hint) { Checked = _sources.Controls.Count == 0 });
        _sources.ClientSizeChanged += (_, _) => FitSourceOptions();
        DrawerMetrics.Bind(_sources, FitSourceOptions);
        grid.Controls.Add(_sources, 0, 5);
        grid.Controls.Add(_progress, 0, 6);
        _status.Visible = false;
        _status.TextChanged += (_, _) => _status.Visible = _status.Text.Length != 0;
        grid.Controls.Add(_status, 0, 7);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 10, 0, 0) };
        actions.Controls.AddRange([_primary, _later, _cancel, _skip]);
        grid.Controls.Add(actions, 0, 8);
        Controls.Add(grid);
        _cancel.Visible = false;
        _primary.Click += async (_, _) => await RunPrimaryAsync();
        _later.Click += (_, _) => Hide();
        _skip.Click += (_, _) => { Hide(); skip(); };
        _cancel.Click += (_, _) => _transfer?.Cancel();
        CancelButton = _later;
        Shown += (_, _) => _later.Focus();
    }

    /// <summary>空间足够时两条线路等宽并排；窄窗或较大字体时整项换行，不截断来源名称和国内推荐标记。</summary>
    private void FitSourceOptions()
    {
        var options = _sources.Controls.OfType<ReleaseSourceOption>().ToArray();
        int width = _sources.ClientSize.Width;
        if (options.Length == 0 || width <= 0) return;
        int gap = _sources.LogicalToDeviceUnits(8);
        int cell = (width - gap * (options.Length - 1)) / options.Length;
        bool inline = options.All(option => option.GetPreferredSize(Size.Empty).Width <= cell);
        for (int i = 0; i < options.Length; i++)
        {
            var option = options[i];
            bool hasNext = i < options.Length - 1;
            option.Margin = new Padding(0, 0, inline && hasNext ? gap : 0, !inline && hasNext ? gap : 0);
            option.Size = new(inline ? (hasNext ? cell : width - i * (cell + gap)) : width, option.GetPreferredSize(Size.Empty).Height);
        }
    }

    /// <summary>构建按文字高度排版的标题和版本行，为发布说明保留剩余空间。</summary>
    private static Label Caption(string text, Font font, int above = 0) => new()
        { Text = text, Font = font, AutoSize = true, Margin = new Padding(2, above, 2, 3) };

    /// <summary>同一窗口只允许一个下载或交接任务。校验失败不进入可安装状态，用户可以换源重试。</summary>
    private async Task RunPrimaryAsync()
    {
        if (_transfer != null || _installing) return;
        try
        {
            if (_verified != null)
            {
                _installing = true;
                _later.Enabled = _primary.Enabled = false;
                _status.Text = "正在准备安装，完成后将重新打开 SideStage…";
                await _install(_verified);
                if (!IsDisposed && !_lifetime.IsCancellationRequested) _status.Text = "尚未安装，可稍后继续。";
                return;
            }
            var transfer = _transfer = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            _primary.Enabled = _sources.Enabled = false;
            _primary.Text = "下载中";
            _cancel.Visible = _progress.Visible = true;
            _progress.Value = 0;
            _skip.Visible = false;
            _progress.Style = ProgressBarStyle.Marquee;
            _status.Text = "正在校验更新包信息…";
            _later.Text = "后台下载";
            var source = _sources.Controls.OfType<ReleaseSourceOption>().Single(r => r.Checked).Source;
            var progress = new Progress<int>(value =>
            {
                // 取消或换源后，上一轮排队的进度不能覆盖新下载或「等待安装」状态。
                if (IsDisposed || _transfer != transfer) return;
                _progress.Style = value < 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
                if (value >= 0) _progress.Value = value;
                _status.Text = value < 0 ? "正在下载安装包…" : $"正在下载安装包：{value}%";
            });
            _verified = await _download(source, progress, _transfer.Token);
            if (_lifetime.IsCancellationRequested) return;
            _status.Text = "下载完成，安装包已校验。安装后将重新打开 SideStage。";
            _primary.Text = "安装并重启";
            _readyToInstall?.Invoke();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or HttpRequestException or
            CryptographicException or FormatException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
        {
            if (_lifetime.IsCancellationRequested || IsDisposed) return;
            _status.Text = ex is OperationCanceledException ? "操作已取消或超时。" : $"未完成：{ex.Message}";
            if (_verified == null) { _status.Text += " 可换源重试。"; _primary.Text = "重新下载"; }
        }
        finally
        {
            _transfer?.Dispose();
            _transfer = null;
            _installing = false;
            if (!IsDisposed && !_lifetime.IsCancellationRequested)
            {
                _primary.Enabled = _later.Enabled = true;
                _later.Text = "稍后";
                _skip.Visible = _verified == null;
                if (!_sources.IsDisposed) _sources.Enabled = _verified == null;
                _cancel.Visible = _progress.Visible = false;
            }
        }
    }

    /// <summary>用户点叉的效果等同「稍后」。主窗体退出时仍允许正常释放控件。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.CloseReason != CloseReason.UserClosing || _lifetime.IsCancellationRequested) return;
        e.Cancel = true;
        if (!_installing) Hide();
    }

    /// <summary>窗体结束时取消传输并释放私有字体；收起窗口时不调用此方法。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) { _transfer?.Cancel(); _body.Dispose(); _heading.Dispose(); _section.Dispose(); _hint.Dispose(); }
        base.Dispose(disposing);
    }
}
