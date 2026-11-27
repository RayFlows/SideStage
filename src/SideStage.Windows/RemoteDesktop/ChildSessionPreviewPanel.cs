/*
 * 文件名称：ChildSessionPreviewPanel.cs
 * 文件职责：
 *   为 RDP 子会话预览控件提供 1:1 滚动显示、自适应布局和点击坐标换算。
 *
 * 显示约束：
 *   - 远端逻辑分辨率来自 ChildSessionPreviewSettings，变更在下一次连接时生效。
 *   - 1:1 模式保持远端像素尺寸，宿主空间不足时提供滚动条。
 *   - 自适应模式按设置等比留边，或填满宿主区域；布局和输入使用同一几何规则。
 */

using System.Drawing;
using System.Windows.Forms;

namespace SideStage.Windows.RemoteDesktop;

/// <summary>
/// 按子会话预览设置承载并缩放 <see cref="ChildSessionPreviewControl"/> 的 WinForms 面板。
/// </summary>
public sealed class ChildSessionPreviewPanel : Panel
{
    private readonly Panel _previewSurface = new();
    // 独立使用面板时提供系统配色的状态提示，同时遮住未经核验的原生窗口。
    private readonly Label _cover = new()
    {
        Dock = DockStyle.Fill,
        BackColor = SystemColors.Control,
        ForeColor = SystemColors.ControlText,
        TextAlign = ContentAlignment.MiddleCenter,
        Text = "子会话尚未建立",
    };

    /// <summary>
    /// 创建一个使用默认 1920×1080、自适应缩放和锁定宽高比的预览面板。
    /// </summary>
    /// <param name="audioMode">本连接的子会话音频输出模式；默认不播放声音。</param>
    public ChildSessionPreviewPanel(ChildSessionAudioMode audioMode = ChildSessionAudioMode.DoNotPlay)
        : this(ChildSessionPreviewSettings.Default, audioMode)
    {
    }

    /// <summary>
    /// 创建一个使用指定远端分辨率和本地显示方式的预览面板。
    /// </summary>
    /// <param name="settings">远端尺寸、缩放模式和比例锁定选项。</param>
    /// <param name="audioMode">本连接的子会话音频输出模式；默认不播放声音。</param>
    public ChildSessionPreviewPanel(
        ChildSessionPreviewSettings settings,
        ChildSessionAudioMode audioMode = ChildSessionAudioMode.DoNotPlay)
        : this(settings, audioMode, null)
    {
    }

    /// <summary>允许宿主装饰原生滚动面板。包装控件负责视口尺寸，远端像素与输入坐标仍由预览库管理。</summary>
    /// <param name="settings">远端尺寸和缩放选项。</param>
    /// <param name="audioMode">本次连接的播放策略。</param>
    /// <param name="wrapViewport">将传入的面板装入宿主控件并返回该控件。传入空值时使用系统滚动条。</param>
    public ChildSessionPreviewPanel(ChildSessionPreviewSettings settings, ChildSessionAudioMode audioMode,
        Func<Panel, Control>? wrapViewport)
    {
        ArgumentNullException.ThrowIfNull(settings);
        BackColor = Color.FromArgb(12, 14, 16);
        BorderStyle = BorderStyle.FixedSingle;
        AutoScroll = false;
        _previewSurface.BackColor = BackColor;
        _previewSurface.Dock = DockStyle.Fill;
        _previewSurface.AutoScroll = false;

        PreviewControl = new ChildSessionPreviewControl(settings, audioMode)
        {
            Dock = DockStyle.None,
            Size = settings.Resolution.ToSize(),
            Location = Point.Empty,
            Enabled = false,
        };

        _previewSurface.Controls.Add(PreviewControl);
        Control host = wrapViewport?.Invoke(_previewSurface) ?? _previewSurface;
        if (host != _previewSurface && !host.Contains(_previewSurface))
            throw new ArgumentException("视口必须包含传入的滚动面板。", nameof(wrapViewport));
        host.Dock = DockStyle.Fill;
        Controls.Add(host);

        Controls.Add(_cover);
        _cover.BringToFront();
    }

    /// <summary>
    /// 获取由当前面板拥有的 RDP 预览控件。
    /// </summary>
    public ChildSessionPreviewControl PreviewControl { get; }

    /// <summary>
    /// 获取当前预览面板使用的分辨率和显示选项。
    /// </summary>
    public ChildSessionPreviewSettings Settings => PreviewControl.Settings;

    /// <summary>当前是否已开放经过目标核验的系统登录或桌面准备画面，供宿主显示对应状态。</summary>
    public bool IsPresentingChildSignIn { get; private set; }

    /// <summary>
    /// 更新分辨率和显示方式，并立即重新布局；连接中的分辨率变更会由预览控件拒绝。
    /// </summary>
    /// <param name="settings">通过构造函数完成校验的完整预览设置。</param>
    public void SetSettings(ChildSessionPreviewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PreviewControl.SetSettings(settings);
        PerformLayout();
    }

    /// <summary>
    /// 将宿主客户区坐标映射为当前远端像素；留边及远端桌面以外的区域返回 null。
    /// </summary>
    /// <param name="hostPoint">面板客户区坐标。</param>
    /// <returns>远端坐标或 null。</returns>
    public Point? TryMapToRemotePoint(Point hostPoint)
    {
        if (!ClientRectangle.Contains(hostPoint))
        {
            return null;
        }

        Point surfacePoint = _previewSurface.PointToClient(PointToScreen(hostPoint));
        if (!_previewSurface.ClientRectangle.Contains(surfacePoint))
        {
            // 1:1 模式启用滚动条；滚动条和面板边缘不是远端桌面的一部分。
            return null;
        }

        ChildSessionPreviewSettings settings = Settings;
        if (settings.ScalingMode == PreviewScalingMode.OneToOne)
        {
            // 1:1 模式按滚动后 ActiveX 实际收到的原始像素位置映射。
            Point remotePoint = PreviewControl.PointToClient(PointToScreen(hostPoint));
            return PreviewViewport.ToRemotePoint(
                remotePoint,
                PreviewControl.Size,
                settings.Resolution.ToSize(),
                PreviewScalingMode.OneToOne);
        }

        return PreviewViewport.ToRemotePoint(
            surfacePoint,
            _previewSurface.ClientSize,
            settings.Resolution.ToSize(),
            settings.ScalingMode,
            settings.PreserveAspectRatio);
    }

    /// <summary>
    /// 根据当前 RDP 连接状态更新预览画面和空状态提示。
    /// </summary>
    /// <param name="connectionState">RDP ActiveX 当前连接状态。</param>
    /// <param name="hasChildSession">当前父会话是否已经报告子会话。</param>
    /// <param name="childSessionsEnabled">Windows 是否已启用子会话能力。</param>
    /// <param name="isVerifiedChildSession">连接目标是否已核验为活动子会话。</param>
    /// <param name="verificationMessage">正在核验或核验失败时需要展示的原因。</param>
    /// <remarks>
    /// 保持 ActiveX 可创建，使用不透明提示层遮住未确认的目标。得到独立子会话身份后
    /// 允许用户在 Windows 自己的画面中登录，自动操作仍须通过活动会话核验。
    /// </remarks>
    public void SetPreviewState(
        RdpPreviewConnectionState connectionState,
        bool hasChildSession,
        bool childSessionsEnabled,
        bool isVerifiedChildSession,
        string? verificationMessage = null)
    {
        IsPresentingChildSignIn = connectionState == RdpPreviewConnectionState.Connected
            && hasChildSession && !isVerifiedChildSession && PreviewControl.CanPresentChildSignIn();
        bool canDisplayRemoteContent = connectionState == RdpPreviewConnectionState.Connected
            && (isVerifiedChildSession || IsPresentingChildSignIn);
        // 隐藏 WinForms AxHost 会跳过首次 CreateControl；保留句柄，用遮罩和禁用输入隔离等待状态。
        PreviewControl.Enabled = canDisplayRemoteContent;

        (string title, string description) = connectionState switch
        {
            RdpPreviewConnectionState.Connecting =>
                ("正在连接本机子会话", "等待 Windows 返回会话状态"),
            RdpPreviewConnectionState.Disconnected when !childSessionsEnabled =>
                ("Windows 子会话功能未启用", "启用子会话功能后才能连接预览"),
            RdpPreviewConnectionState.Disconnected when hasChildSession =>
                ("子会话预览未连接", "点击“连接预览”打开子会话"),
            RdpPreviewConnectionState.Disconnected =>
                ("子会话尚未建立", "点击“连接预览”发起本机子会话连接"),
            RdpPreviewConnectionState.Connected when !isVerifiedChildSession =>
                ("正在核验预览目标", verificationMessage ?? "目标确认前，预览画面保持隐藏"),
            _ => (string.Empty, string.Empty),
        };
        _cover.Text = string.IsNullOrEmpty(description) ? title : $"{title}\r\n{description}";
        _cover.Visible = !canDisplayRemoteContent;
    }

    /// <summary>
    /// 在读取会话状态失败时隐藏 RDP 控件并展示可诊断的错误原因。
    /// </summary>
    /// <param name="message">供用户查看的状态读取失败信息。</param>
    public void SetPreviewUnavailable(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        IsPresentingChildSignIn = false;
        PreviewControl.Enabled = false;
        _cover.Text = $"会话状态不可用\r\n{message}";
        _cover.Visible = true;
    }

    /// <summary>
    /// 在容器尺寸变化或子控件布局变化后，按当前缩放模式重新计算预览矩形。
    /// </summary>
    /// <param name="levent">WinForms 布局事件参数。</param>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);

        // WinForms 基类构造期间可能虚分派到此方法；此时自动属性尚未完成初始化。
        // 跳过这一次布局，待构造函数添加预览控件后由后续布局回调完成定位。
        if (PreviewControl is null)
        {
            return;
        }

        ChildSessionPreviewSettings settings = Settings;
        Size source = settings.Resolution.ToSize();
        Rectangle viewport;

        if (settings.ScalingMode == PreviewScalingMode.OneToOne)
        {
            // 保持远端桌面原始像素大小；需要滚动时由面板提供滚动条。
            if (!_previewSurface.AutoScroll)
            {
                _previewSurface.AutoScroll = true;
                _previewSurface.AutoScrollMinSize = source;
            }
            else if (_previewSurface.AutoScrollMinSize != source)
            {
                _previewSurface.AutoScrollMinSize = source;
            }

            Point scrollOffset = _previewSurface.AutoScrollPosition;
            viewport = new Rectangle(
                scrollOffset.X,
                scrollOffset.Y,
                source.Width,
                source.Height);
        }
        else
        {
            if (_previewSurface.AutoScroll)
            {
                _previewSurface.AutoScrollPosition = Point.Empty;
                _previewSurface.AutoScroll = false;
                _previewSurface.AutoScrollMinSize = Size.Empty;
            }

            viewport = PreviewViewport.Calculate(
                _previewSurface.ClientSize.Width,
                _previewSurface.ClientSize.Height,
                source.Width,
                source.Height,
                settings.PreserveAspectRatio);
        }

        if (PreviewControl.Bounds != viewport)
        {
            PreviewControl.Bounds = viewport;
        }

        // ActiveX 有独立窗口句柄，布局变化时仍须让遮罩挡在预览表面前方。
        if (_cover?.Visible == true && _cover.Parent == this) _cover.BringToFront();
    }
}
