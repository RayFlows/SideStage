/*
 * 文件名称：ClassicPreviewForm.cs
 * 文件职责：
 *   提供子会话库的人工验收界面，展示当前会话、子会话和预览连接状态。
 *
 * 操作安全边界：
 *   - 启动和定时刷新只读取状态。
 *   - “连接预览”发起本机子会话连接。
 *   - “断开预览”只断开 ActiveX 预览，不结束子会话或内部进程。
 *   - 音频切换会自动重连 ActiveX 预览，不注销子会话。
 *   - “注销子会话”会结束该子会话中的全部用户进程，必须经二次确认。
 *   - 窗口关闭时不注销子会话。
 */

using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SideStage.Core.Platform.Windows;
using SideStage.Core.Sessions;
using SideStage.Windows.Platform.Windows;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App;

/// <summary>
/// 子会话预览的人工验收窗口。
/// </summary>
internal sealed class ClassicPreviewForm : Form
{
    private static readonly Color WindowColor = Color.FromArgb(22, 24, 27);
    private static readonly Color SurfaceColor = Color.FromArgb(30, 33, 37);
    private static readonly Color RaisedColor = Color.FromArgb(39, 43, 48);
    private static readonly Color BorderColor = Color.FromArgb(53, 58, 64);
    private static readonly Color TextColor = Color.FromArgb(235, 238, 240);
    private static readonly Color MutedTextColor = Color.FromArgb(153, 162, 169);
    private static readonly Color AccentColor = Color.FromArgb(117, 211, 164);
    private static readonly Color WarningColor = Color.FromArgb(235, 190, 104);
    private static readonly Color ErrorColor = Color.FromArgb(226, 125, 125);
    private const int DwmAttributeBorderColor = 34;
    private const int DwmAttributeCaptionColor = 35;
    private const int DwmAttributeTextColor = 36;
    private const int DwmAttributeImmersiveDarkMode = 20;

    private readonly WindowsSessionInspector _sessionInspector = new();
    private readonly WindowsChildSessionTerminator _sessionTerminator = new();
    private readonly WindowsChildSessionCredentialStore _credentialStore = new();
    private readonly ChildSessionPreviewPanel _previewPanel = new();
    private readonly ThemedButton _connectButton = new();
    private readonly ThemedButton _credentialButton = new();
    private readonly ThemedButton _disconnectButton = new();
    private readonly ThemedButton _logoffButton = new();
    private readonly CheckBox _muteChildAudioToggle = new();
    private readonly ToolTip _audioModeToolTip = new();
    private readonly ComboBox _resolutionSelector = new();
    private readonly Label _customWidthLabel = new();
    private readonly Label _customHeightLabel = new();
    private readonly NumericUpDown _customWidthSelector = new();
    private readonly NumericUpDown _customHeightSelector = new();
    private readonly ComboBox _scalingModeSelector = new();
    private readonly CheckBox _preserveAspectRatioToggle = new();
    private readonly Label _displaySettingsHelp = new();
    private readonly Label _statusLabel = new();
    private readonly Label _parentSessionValue = new();
    private readonly Label _capabilityValue = new();
    private readonly Label _childSessionValue = new();
    private readonly Label _previewStateValue = new();
    private readonly ListBox _eventLog = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private string _lastObservedState = string.Empty;
    private string _lastVerifiedChild = string.Empty;
    private string _lastTransientStatus = string.Empty;
    private bool _refreshInProgress;
    private bool _childNeedsUnlock;
    private readonly AudioPreviewReconnect _audioReconnect = new();
    private bool _synchronizingDisplaySettings;

    /// <summary>
    /// 建立预览控件、操作按钮、状态区域和只读轮询。
    /// </summary>
    public ClassicPreviewForm()
    {
        Program.ApplyIdentity(this);
        BackColor = WindowColor;
        ForeColor = TextColor;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
        MinimumSize = new Size(800, 600);
        Size = new Size(1120, 760);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        ConfigureRefreshTimer();
        RefreshSessionStatus();
        FormClosed += (_, _) =>
        {
            _refreshTimer.Dispose();
            _audioModeToolTip.Dispose();
        };
    }

    /// <summary>
    /// 在窗口句柄创建后将 Windows 11 非客户区配色与应用内暗色主题对齐。
    /// </summary>
    /// <param name="eventArgs">窗口句柄创建事件参数。</param>
    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);

        int darkModeEnabled = 1;
        _ = DwmSetWindowAttribute(
            Handle,
            DwmAttributeImmersiveDarkMode,
            ref darkModeEnabled,
            sizeof(int));
        SetDwmColor(Handle, DwmAttributeCaptionColor, WindowColor);
        SetDwmColor(Handle, DwmAttributeBorderColor, WindowColor);
        SetDwmColor(Handle, DwmAttributeTextColor, TextColor);
    }

    /// <summary>
    /// 创建不依赖 Designer 的窗口布局。
    /// </summary>
    private void BuildLayout()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(14),
            BackColor = WindowColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));

        Panel header = BuildHeader();
        FlowLayoutPanel toolbar = BuildToolbar();
        TableLayoutPanel workspace = BuildWorkspace();
        Panel activityLog = BuildActivityLog();

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(toolbar, 0, 1);
        root.Controls.Add(workspace, 0, 2);
        root.Controls.Add(activityLog, 0, 3);
        Controls.Add(root);
    }

    /// <summary>
    /// 设置 Windows 桌面窗口管理器的非客户区颜色。
    /// </summary>
    /// <param name="attribute">DWM 窗口颜色属性编号。</param>
    /// <param name="color">需要显示的 RGB 颜色。</param>
    /// <remarks>
    /// 该调用只影响标题栏外观；Windows 不支持该颜色属性时保留系统默认标题栏。
    /// </remarks>
    private static void SetDwmColor(IntPtr windowHandle, int attribute, Color color)
    {
        int colorRef = color.R | (color.G << 8) | (color.B << 16);
        _ = DwmSetWindowAttribute(
            windowHandle,
            attribute,
            ref colorRef,
            sizeof(int));
    }

    /// <summary>
    /// 设置指定窗口的 DWM 非客户区颜色属性。
    /// </summary>
    /// <param name="windowHandle">已创建的顶层窗口句柄。</param>
    /// <param name="attribute">DWM 窗口颜色属性编号。</param>
    /// <param name="value">Win32 COLORREF 格式的颜色值。</param>
    /// <param name="valueSize">属性值占用的字节数。</param>
    /// <returns>Windows DWM API 的 HRESULT。</returns>
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);

    /// <summary>
    /// 创建品牌标题和总连接状态组成的窗口顶栏。
    /// </summary>
    /// <returns>已完成布局的标题栏。</returns>
    private Panel BuildHeader()
    {
        Panel header = new()
        {
            Dock = DockStyle.Fill,
            BackColor = WindowColor,
        };

        Panel brandMark = new()
        {
            BackColor = AccentColor,
            Location = new Point(0, 10),
            Size = new Size(3, 27),
        };

        Label title = new()
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            ForeColor = TextColor,
            Location = new Point(14, 6),
            Text = Program.DisplayName,
        };

        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _statusLabel.ForeColor = WarningColor;
        _statusLabel.TextAlign = ContentAlignment.MiddleRight;
        _statusLabel.Dock = DockStyle.Right;
        _statusLabel.Padding = new Padding(8, 0, 2, 0);

        header.Controls.Add(brandMark);
        header.Controls.Add(title);
        header.Controls.Add(_statusLabel);
        return header;
    }

    /// <summary>
    /// 创建操作栏，并用主次视觉区分连接、断开和注销操作。
    /// </summary>
    /// <returns>承载所有会话操作按钮的工具栏。</returns>
    private FlowLayoutPanel BuildToolbar()
    {
        FlowLayoutPanel toolbar = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 7),
            BackColor = WindowColor,
        };

        ConfigureButton(_connectButton, "连接", ConnectPreview, AccentColor, WindowColor);
        ConfigureButton(_credentialButton, "登录凭据", ManageCredentials, RaisedColor, TextColor);
        ConfigureButton(_disconnectButton, "断开连接", DisconnectPreview, RaisedColor, TextColor);
        ConfigureButton(_logoffButton, "注销子会话", LogoffChildSession, SurfaceColor, ErrorColor);
        _muteChildAudioToggle.AutoSize = false;
        _muteChildAudioToggle.Size = new Size(110, 36);
        _muteChildAudioToggle.Text = "静音子会话";
        _muteChildAudioToggle.Checked = true;
        _muteChildAudioToggle.FlatStyle = FlatStyle.Flat;
        _muteChildAudioToggle.BackColor = WindowColor;
        _muteChildAudioToggle.ForeColor = TextColor;
        _muteChildAudioToggle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
        _muteChildAudioToggle.Margin = new Padding(4, 0, 8, 0);
        _muteChildAudioToggle.Cursor = Cursors.Hand;
        _muteChildAudioToggle.CheckedChanged += ChangeChildAudioMode;
        _audioModeToolTip.SetToolTip(
            _muteChildAudioToggle,
            "勾选时不播放子会话声音；取消勾选会将声音转发到本机。连接中也可切换，会短暂重连预览但不会注销子会话；若凭据未保存，可能需要重新登录。");
        _disconnectButton.Enabled = false;
        _logoffButton.Enabled = false;

        toolbar.Controls.Add(_connectButton);
        toolbar.Controls.Add(_muteChildAudioToggle);
        toolbar.Controls.Add(_credentialButton);
        toolbar.Controls.Add(_disconnectButton);
        toolbar.Controls.Add(_logoffButton);
        return toolbar;
    }

    /// <summary>
    /// 应用工具栏中的子会话静音选项；活动预览会自动断开并重新连接以应用新模式。
    /// </summary>
    /// <param name="sender">静音复选框。</param>
    /// <param name="eventArgs">复选框状态变化参数。</param>
    private void ChangeChildAudioMode(object? sender, EventArgs eventArgs)
    {
        if (_audioReconnect.Active) return;
        ChildSessionAudioMode audioMode = _muteChildAudioToggle.Checked
            ? ChildSessionAudioMode.DoNotPlay
            : ChildSessionAudioMode.RedirectToThisComputer;
        try
        {
            _muteChildAudioToggle.Enabled = false;
            _audioReconnect.Change(_previewPanel.PreviewControl, audioMode);

            string modeMessage = _muteChildAudioToggle.Checked
                ? "子会话声音已设为关闭"
                : "子会话声音已设为转发到本机";
            AppendLog(_audioReconnect.Active
                ? $"{modeMessage}；正在重新连接预览，子会话不会注销。"
                : $"{modeMessage}；将在下次连接预览时生效。");
            RefreshSessionStatus();
        }
        catch (Exception exception) when (IsDisplayableOperationFailure(exception))
        {
            // 断开请求失败时保留原模式，并让复选框继续反映实际设置。
            _muteChildAudioToggle.CheckedChanged -= ChangeChildAudioMode;
            _muteChildAudioToggle.Checked =
                _previewPanel.PreviewControl.AudioMode == ChildSessionAudioMode.DoNotPlay;
            _muteChildAudioToggle.CheckedChanged += ChangeChildAudioMode;
            ShowOperationError("无法更改子会话声音设置", exception);
        }
    }

    /// <summary>
    /// 组合主预览区域与会话信息侧栏，保持预览为窗口主要视觉区域。
    /// </summary>
    /// <returns>带有固定状态栏和可伸缩预览区的工作区。</returns>
    private TableLayoutPanel BuildWorkspace()
    {
        TableLayoutPanel workspace = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = WindowColor,
            Padding = new Padding(0, 3, 0, 10),
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 232));

        TableLayoutPanel previewColumn = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = WindowColor,
        };
        previewColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        previewColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Panel previewHeading = new()
        {
            Dock = DockStyle.Fill,
            BackColor = WindowColor,
        };
        Label previewTitle = new()
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = TextColor,
            Location = new Point(0, 4),
            Text = "实时预览",
        };
        previewHeading.Controls.Add(previewTitle);

        _previewPanel.Dock = DockStyle.Fill;
        _previewPanel.Margin = new Padding(0, 0, 12, 0);
        previewColumn.Controls.Add(previewHeading, 0, 0);
        previewColumn.Controls.Add(_previewPanel, 0, 1);

        Panel sessionSidebar = BuildSessionSidebar();
        workspace.Controls.Add(previewColumn, 0, 0);
        workspace.Controls.Add(sessionSidebar, 1, 0);
        return workspace;
    }

    /// <summary>
    /// 构建紧凑的会话信息区，分别展示父会话、能力开关、子会话和预览状态。
    /// </summary>
    /// <returns>会话信息侧栏。</returns>
    private Panel BuildSessionSidebar()
    {
        Panel sidebar = new()
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceColor,
            Padding = new Padding(16, 14, 16, 10),
            AutoScroll = true,
        };

        Label heading = new()
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = TextColor,
            Location = new Point(16, 12),
            Text = "会话概览",
        };

        Label resolutionTitle = new()
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular),
            ForeColor = MutedTextColor,
            Location = new Point(16, 210),
            Text = "远端分辨率",
        };

        _resolutionSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _resolutionSelector.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
        _resolutionSelector.Location = new Point(16, 228);
        _resolutionSelector.Size = new Size(184, 28);
        _resolutionSelector.Items.AddRange(
            RemoteDesktopResolution.Common
                .Select(resolution => new ResolutionOption(resolution.ToString(), resolution))
                .Append(new ResolutionOption("自定义尺寸…", null))
                .Cast<object>()
                .ToArray());

        _customWidthLabel.AutoSize = true;
        _customWidthLabel.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular);
        _customWidthLabel.ForeColor = MutedTextColor;
        _customWidthLabel.Location = new Point(16, 263);
        _customWidthLabel.Text = "宽";
        _customHeightLabel.AutoSize = true;
        _customHeightLabel.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular);
        _customHeightLabel.ForeColor = MutedTextColor;
        _customHeightLabel.Location = new Point(110, 263);
        _customHeightLabel.Text = "高";
        ConfigureResolutionInput(_customWidthSelector, new Point(36, 260));
        ConfigureResolutionInput(_customHeightSelector, new Point(130, 260));

        Label scalingModeTitle = new()
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular),
            ForeColor = MutedTextColor,
            Location = new Point(16, 293),
            Text = "预览缩放",
        };
        _scalingModeSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _scalingModeSelector.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
        _scalingModeSelector.Location = new Point(16, 311);
        _scalingModeSelector.Size = new Size(184, 28);
        _scalingModeSelector.Items.AddRange(
        [
            new ScalingModeOption("1:1 原像素", PreviewScalingMode.OneToOne),
            new ScalingModeOption("自适应", PreviewScalingMode.Adaptive),
        ]);

        _preserveAspectRatioToggle.AutoSize = false;
        _preserveAspectRatioToggle.Location = new Point(13, 344);
        _preserveAspectRatioToggle.Size = new Size(188, 25);
        _preserveAspectRatioToggle.Text = "保持宽高比";
        _preserveAspectRatioToggle.FlatStyle = FlatStyle.Flat;
        _preserveAspectRatioToggle.BackColor = SurfaceColor;
        _preserveAspectRatioToggle.ForeColor = TextColor;
        _preserveAspectRatioToggle.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular);
        _preserveAspectRatioToggle.Cursor = Cursors.Hand;

        _displaySettingsHelp.AutoSize = false;
        _displaySettingsHelp.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular);
        _displaySettingsHelp.ForeColor = MutedTextColor;
        _displaySettingsHelp.Location = new Point(16, 371);
        _displaySettingsHelp.Size = new Size(184, 44);
        _displaySettingsHelp.Text = "分辨率在连接时生效；自适应可按比例留边或铺满。";

        _resolutionSelector.SelectedIndexChanged += ChangeDisplaySettings;
        _customWidthSelector.ValueChanged += ChangeDisplaySettings;
        _customHeightSelector.ValueChanged += ChangeDisplaySettings;
        _scalingModeSelector.SelectedIndexChanged += ChangeDisplaySettings;
        _preserveAspectRatioToggle.CheckedChanged += ChangeDisplaySettings;

        AddSessionValue(sidebar, "父会话", _parentSessionValue, 54);
        AddSessionValue(sidebar, "子会话功能", _capabilityValue, 94);
        AddSessionValue(sidebar, "子会话", _childSessionValue, 134);
        AddSessionValue(sidebar, "预览", _previewStateValue, 174);
        sidebar.Controls.Add(heading);
        sidebar.Controls.Add(resolutionTitle);
        sidebar.Controls.Add(_resolutionSelector);
        sidebar.Controls.Add(_customWidthLabel);
        sidebar.Controls.Add(_customHeightLabel);
        sidebar.Controls.Add(_customWidthSelector);
        sidebar.Controls.Add(_customHeightSelector);
        sidebar.Controls.Add(scalingModeTitle);
        sidebar.Controls.Add(_scalingModeSelector);
        sidebar.Controls.Add(_preserveAspectRatioToggle);
        sidebar.Controls.Add(_displaySettingsHelp);
        SynchronizeDisplaySettingsControls(_previewPanel.Settings);
        return sidebar;
    }

    /// <summary>
    /// 配置一个用于输入远端桌面宽度或高度的数值控件。
    /// </summary>
    /// <param name="selector">需要初始化的数值控件。</param>
    /// <param name="location">数值控件在侧栏中的位置。</param>
    private static void ConfigureResolutionInput(NumericUpDown selector, Point location)
    {
        selector.Minimum = 200;
        selector.Maximum = 8192;
        selector.Increment = 1;
        selector.ThousandsSeparator = true;
        selector.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
        selector.Location = location;
        selector.Size = new Size(70, 26);
    }

    /// <summary>
    /// 由设置控件修改远端分辨率和本地预览显示策略。
    /// </summary>
    /// <param name="sender">发生变化的下拉框、数值框或复选框。</param>
    /// <param name="eventArgs">控件状态变化参数。</param>
    private void ChangeDisplaySettings(object? sender, EventArgs eventArgs)
    {
        if (_synchronizingDisplaySettings
            || _resolutionSelector.SelectedItem is not ResolutionOption resolutionOption
            || _scalingModeSelector.SelectedItem is not ScalingModeOption scalingOption)
        {
            return;
        }

        try
        {
            RemoteDesktopResolution resolution = resolutionOption.Resolution
                ?? new RemoteDesktopResolution(
                    decimal.ToInt32(_customWidthSelector.Value),
                    decimal.ToInt32(_customHeightSelector.Value));
            ChildSessionPreviewSettings settings = new(
                resolution,
                scalingOption.Mode,
                _preserveAspectRatioToggle.Checked);
            _previewPanel.SetSettings(settings);
            SynchronizeDisplaySettingsControls(
                settings,
                keepCustomResolution: resolutionOption.Resolution is null);
            AppendLog(
                $"预览设置：远端 {settings.Resolution}，" +
                $"{(settings.ScalingMode == PreviewScalingMode.OneToOne ? "1:1 原像素" : "自适应")}，" +
                $"{(settings.PreserveAspectRatio ? "保持比例" : "铺满区域")}。");
        }
        catch (Exception exception) when (IsDisplayableOperationFailure(exception))
        {
            SynchronizeDisplaySettingsControls(_previewPanel.Settings);
            ShowOperationError("无法更改预览显示设置", exception);
        }

        UpdateDisplaySettingsAvailability(_previewPanel.PreviewControl.GetConnectionState());
    }

    /// <summary>
    /// 将面板当前设置同步到分辨率、缩放模式和比例锁定控件。
    /// </summary>
    /// <param name="settings">当前由预览面板持有的设置。</param>
    private void SynchronizeDisplaySettingsControls(
        ChildSessionPreviewSettings settings,
        bool keepCustomResolution = false)
    {
        _synchronizingDisplaySettings = true;
        try
        {
            ResolutionOption? matchingResolution = _resolutionSelector.Items
                .Cast<object>()
                .OfType<ResolutionOption>()
                .FirstOrDefault(option => option.Resolution == settings.Resolution);
            _resolutionSelector.SelectedItem = matchingResolution is not null && !keepCustomResolution
                ? matchingResolution
                : _resolutionSelector.Items
                    .Cast<object>()
                    .OfType<ResolutionOption>()
                    .Single(option => option.Resolution is null);
            _customWidthSelector.Value = settings.Resolution.Width;
            _customHeightSelector.Value = settings.Resolution.Height;
            _scalingModeSelector.SelectedItem = _scalingModeSelector.Items
                .Cast<object>()
                .OfType<ScalingModeOption>()
                .Single(option => option.Mode == settings.ScalingMode);
            _preserveAspectRatioToggle.Checked = settings.PreserveAspectRatio;
        }
        finally
        {
            _synchronizingDisplaySettings = false;
        }
    }

    /// <summary>
    /// 按连接状态控制可以即时应用的选项，并解释远端分辨率的生效时机。
    /// </summary>
    /// <param name="previewState">RDP ActiveX 当前连接状态。</param>
    private void UpdateDisplaySettingsAvailability(RdpPreviewConnectionState previewState)
    {
        bool disconnected = previewState == RdpPreviewConnectionState.Disconnected;
        bool canChangeScaling = previewState != RdpPreviewConnectionState.Connecting;
        bool isCustomResolution = _resolutionSelector.SelectedItem is ResolutionOption { Resolution: null };

        _resolutionSelector.Enabled = disconnected;
        _customWidthSelector.Enabled = disconnected && isCustomResolution;
        _customHeightSelector.Enabled = disconnected && isCustomResolution;
        _customWidthLabel.Visible = isCustomResolution;
        _customHeightLabel.Visible = isCustomResolution;
        _customWidthSelector.Visible = isCustomResolution;
        _customHeightSelector.Visible = isCustomResolution;
        _scalingModeSelector.Enabled = canChangeScaling;
        _preserveAspectRatioToggle.Enabled =
            canChangeScaling
            && _scalingModeSelector.SelectedItem is ScalingModeOption { Mode: PreviewScalingMode.Adaptive };
        bool oneToOne =
            _scalingModeSelector.SelectedItem is ScalingModeOption { Mode: PreviewScalingMode.OneToOne };
        _displaySettingsHelp.Text = previewState == RdpPreviewConnectionState.Connecting
            ? "连接建立期间不能修改显示设置。"
            : oneToOne
                ? disconnected
                    ? "1:1 按远端像素显示；画面超出预览区时可滚动查看。"
                    : "1:1 按远端像素显示；分辨率需断开后更改。"
                : disconnected
                    ? "分辨率在连接时生效；自适应可保持比例留边，或铺满并拉伸。"
                    : "分辨率需断开后更改；自适应缩放可即时切换。";
    }

    /// <summary>
    /// 在会话侧栏中添加一组标题和值，并设置一致的文字层级。
    /// </summary>
    /// <param name="container">承载字段的侧栏面板。</param>
    /// <param name="title">字段显示名称。</param>
    /// <param name="valueLabel">用于呈现动态状态值的标签。</param>
    /// <param name="top">字段相对于侧栏顶部的位置。</param>
    private static void AddSessionValue(
        Control container,
        string title,
        Label valueLabel,
        int top)
    {
        Label titleLabel = new()
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular),
            ForeColor = MutedTextColor,
            Location = new Point(16, top),
            Text = title,
        };

        valueLabel.AutoSize = false;
        valueLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        valueLabel.ForeColor = TextColor;
        valueLabel.Location = new Point(16, top + 17);
        valueLabel.Size = new Size(184, 20);
        valueLabel.Text = "读取中";

        container.Controls.Add(titleLabel);
        container.Controls.Add(valueLabel);
    }

    /// <summary>
    /// 创建带有标题行和自绘文字颜色的活动日志区域。
    /// </summary>
    /// <returns>底部活动日志面板。</returns>
    private Panel BuildActivityLog()
    {
        Panel panel = new()
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceColor,
            Padding = new Padding(12, 30, 12, 8),
        };

        Label heading = new()
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold),
            ForeColor = MutedTextColor,
            Location = new Point(12, 7),
            Text = "活动记录",
        };

        _eventLog.Dock = DockStyle.Fill;
        _eventLog.BackColor = SurfaceColor;
        _eventLog.BorderStyle = BorderStyle.None;
        _eventLog.DrawMode = DrawMode.OwnerDrawFixed;
        _eventLog.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular);
        _eventLog.ForeColor = TextColor;
        _eventLog.HorizontalScrollbar = false;
        _eventLog.IntegralHeight = false;
        _eventLog.ItemHeight = 19;
        _eventLog.Location = new Point(12, 30);
        _eventLog.Size = new Size(300, 90);
        _eventLog.DrawItem += DrawActivityLogItem;

        panel.Controls.Add(heading);
        panel.Controls.Add(_eventLog);
        return panel;
    }

    /// <summary>
    /// 设置工具栏按钮的尺寸、颜色和操作事件。
    /// </summary>
    /// <param name="button">需要配置的按钮实例。</param>
    /// <param name="text">按钮显示名称。</param>
    /// <param name="onClick">用户点击时执行的操作。</param>
    /// <param name="backgroundColor">按钮背景颜色。</param>
    /// <param name="foregroundColor">按钮文字颜色。</param>
    private static void ConfigureButton(
        ThemedButton button,
        string text,
        EventHandler onClick,
        Color backgroundColor,
        Color foregroundColor)
    {
        button.Text = text;
        button.AutoSize = false;
        button.Size = new Size(text == "注销子会话" ? 132 : 116, 36);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.BorderColor = BorderColor;
        button.BackColor = backgroundColor;
        button.ForeColor = foregroundColor;
        button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        button.Margin = new Padding(0, 0, 8, 0);
        button.Cursor = Cursors.Hand;
        button.Click += onClick;
    }

    /// <summary>
    /// 表示分辨率下拉框中的一项；空分辨率表示使用自定义宽高输入框。
    /// </summary>
    private sealed record ResolutionOption(
        string Label,
        RemoteDesktopResolution? Resolution)
    {
        /// <summary>
        /// 返回下拉框中供用户阅读的选项名称。
        /// </summary>
        /// <returns>分辨率或自定义尺寸标签。</returns>
        public override string ToString() => Label;
    }

    /// <summary>
    /// 表示预览缩放模式下拉框中的一项。
    /// </summary>
    private sealed record ScalingModeOption(
        string Label,
        PreviewScalingMode Mode)
    {
        /// <summary>
        /// 返回下拉框中供用户阅读的缩放模式名称。
        /// </summary>
        /// <returns>简短的中文模式说明。</returns>
        public override string ToString() => Label;
    }

    /// <summary>
    /// 为深色主题提供可读的按钮禁用、悬停和焦点视觉状态。
    /// </summary>
    private sealed class ThemedButton : Button
    {
        private bool _isPointerOver;

        /// <summary>
        /// 启用按钮的双缓冲和自绘模式，避免默认系统按钮与主题冲突。
        /// </summary>
        public ThemedButton()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                value: true);
        }

        /// <summary>
        /// 绘制按钮背景、文字和键盘焦点提示。
        /// </summary>
        /// <param name="eventArgs">按钮绘制事件参数。</param>
        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            Color background = !Enabled
                ? Color.FromArgb(45, 49, 54)
                : _isPointerOver
                    ? ControlPaint.Light(BackColor, 0.08F)
                    : BackColor;
            Color foreground = Enabled ? ForeColor : Color.FromArgb(147, 156, 163);

            using SolidBrush backgroundBrush = new(background);
            eventArgs.Graphics.FillRectangle(backgroundBrush, ClientRectangle);

            if (Enabled && Focused)
            {
                using Pen focusPen = new(AccentColor);
                Rectangle focusBounds = Rectangle.Inflate(ClientRectangle, -3, -3);
                eventArgs.Graphics.DrawRectangle(focusPen, focusBounds);
            }

            TextRenderer.DrawText(
                eventArgs.Graphics,
                Text,
                Font,
                ClientRectangle,
                foreground,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix);
        }

        /// <summary>
        /// 标记鼠标进入按钮，触发悬停颜色重绘。
        /// </summary>
        /// <param name="eventArgs">鼠标进入事件参数。</param>
        protected override void OnMouseEnter(EventArgs eventArgs)
        {
            _isPointerOver = true;
            Invalidate();
            base.OnMouseEnter(eventArgs);
        }

        /// <summary>
        /// 清理鼠标悬停标记，恢复按钮常规颜色。
        /// </summary>
        /// <param name="eventArgs">鼠标离开事件参数。</param>
        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            _isPointerOver = false;
            Invalidate();
            base.OnMouseLeave(eventArgs);
        }
    }

    /// <summary>
    /// 按照日志内容的严重程度绘制活动记录，长文本在区域内省略显示。
    /// </summary>
    /// <param name="sender">触发绘制事件的日志列表。</param>
    /// <param name="eventArgs">绘制区域和状态参数。</param>
    private void DrawActivityLogItem(object? sender, DrawItemEventArgs eventArgs)
    {
        if (eventArgs.Index < 0 || eventArgs.Index >= _eventLog.Items.Count)
        {
            return;
        }

        string message = _eventLog.Items[eventArgs.Index]?.ToString() ?? string.Empty;
        Color textColor = message.Contains("失败", StringComparison.Ordinal)
            ? ErrorColor
            : message.Contains("核验", StringComparison.Ordinal)
                ? WarningColor
                : TextColor;

        using SolidBrush backgroundBrush = new(SurfaceColor);
        eventArgs.Graphics.FillRectangle(backgroundBrush, eventArgs.Bounds);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            message,
            _eventLog.Font,
            eventArgs.Bounds,
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        eventArgs.DrawFocusRectangle();
    }

    /// <summary>
    /// 启动短周期只读刷新，以便在 ActiveX 异步连接后更新状态。
    /// </summary>
    private void ConfigureRefreshTimer()
    {
        _refreshTimer.Interval = 500;
        _refreshTimer.Tick += (_, _) => RefreshSessionStatus();
        _refreshTimer.Start();
    }

    /// <summary>
    /// 由用户明确发起本机子会话预览连接。
    /// </summary>
    /// <param name="sender">触发事件的控件。</param>
    /// <param name="eventArgs">按钮事件参数。</param>
    private void ConnectPreview(object? sender, EventArgs eventArgs)
    {
        try
        {
            using WindowsChildSessionCredentials? savedCredentials = _credentialStore.Read();
            if (savedCredentials is not null)
            {
                _previewPanel.PreviewControl.ConnectPreview(savedCredentials);
                AppendLog("已使用本机保存的登录凭据发出子会话预览连接请求。");
            }
            else
            {
                using CredentialInputDialog dialog = new(_credentialStore);
                if (dialog.ShowDialog(this) != DialogResult.OK
                    || dialog.Credentials is not { } credentials)
                {
                    return;
                }

                // ConnectPreview 会在返回前把凭据交给 ActiveX；SecureString 随即释放。
                using (credentials)
                {
                    _previewPanel.PreviewControl.ConnectPreview(credentials);
                }

                AppendLog("已使用本次输入的登录凭据发出子会话预览连接请求。");
            }
        }
        catch (Exception exception) when (IsDisplayableOperationFailure(exception))
        {
            AppendLog($"连接失败：{exception.Message}");
            ShowOperationError("无法连接子会话预览", exception);
        }

        RefreshSessionStatus();
    }

    /// <summary>
    /// 打开本机凭据管理对话框，以保存、替换或删除应用专属的 Windows 凭据条目。
    /// </summary>
    /// <param name="sender">触发操作的控件。</param>
    /// <param name="eventArgs">按钮事件参数。</param>
    private void ManageCredentials(object? sender, EventArgs eventArgs)
    {
        try
        {
            using CredentialInputDialog dialog = new(_credentialStore, requirePersistentSave: true);
            if (dialog.ShowDialog(this) == DialogResult.OK
                && dialog.Credentials is { } credentials)
            {
                credentials.Dispose();
                AppendLog("已更新本机 Windows 凭据管理器中的子会话登录项。");
            }
        }
        catch (Exception exception) when (IsDisplayableOperationFailure(exception))
        {
            AppendLog($"凭据操作失败：{exception.Message}");
            ShowOperationError("无法管理本机登录凭据", exception);
        }
    }

    /// <summary>
    /// 由用户明确断开 RDP 预览连接，但保留子会话。
    /// </summary>
    /// <param name="sender">触发事件的控件。</param>
    /// <param name="eventArgs">按钮事件参数。</param>
    private void DisconnectPreview(object? sender, EventArgs eventArgs)
    {
        bool canceledAudioReconnect = _audioReconnect.Active;
        bool disconnectIssued = _audioReconnect.Cancel();

        try
        {
            if (!disconnectIssued && _previewPanel.PreviewControl.GetConnectionState() != RdpPreviewConnectionState.Disconnected)
                _previewPanel.PreviewControl.DisconnectPreview();
            AppendLog(canceledAudioReconnect
                ? "已取消音频设置的自动重连并断开预览；子会话不会因该操作注销。"
                : "已断开预览；子会话不会因该操作注销。");
        }
        catch (Exception exception) when (IsDisplayableOperationFailure(exception))
        {
            AppendLog($"断开预览失败：{exception.Message}");
            ShowOperationError("无法断开子会话预览", exception);
        }

        RefreshSessionStatus();
    }

    /// <summary>
    /// 经二次确认后注销当前子会话并结束该会话中的进程。
    /// </summary>
    /// <param name="sender">触发事件的控件。</param>
    /// <param name="eventArgs">按钮事件参数。</param>
    private void LogoffChildSession(object? sender, EventArgs eventArgs)
    {
        DialogResult confirmation = MessageBox.Show(
            this,
            "注销会结束子会话中的所有程序和未保存工作。此操作不可撤销。\r\n\r\n确定继续吗？",
            "确认注销子会话",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        _audioReconnect.Cancel();

        try
        {
            _sessionTerminator.LogoffCurrentChildSession();
            AppendLog("Windows 已接受子会话注销请求。");
        }
        catch (Exception exception) when (IsDisplayableOperationFailure(exception))
        {
            AppendLog($"注销失败：{exception.Message}");
            ShowOperationError("无法注销子会话", exception);
        }

        RefreshSessionStatus();
    }

    /// <summary>
    /// 刷新当前会话、子会话和 RDP 控件状态，并更新可执行操作。
    /// </summary>
    private void RefreshSessionStatus()
    {
        if (_refreshInProgress || IsDisposed)
        {
            return;
        }

        _refreshInProgress = true;

        try
        {
            SessionSnapshot parent = _sessionInspector.GetCurrentSession();
            SessionSnapshot? child = _sessionInspector.TryGetChildSession();
            _childNeedsUnlock = child?.IsLocked == true;
            bool childSessionsEnabled = _sessionInspector.IsChildSessionsEnabled();
            RdpPreviewConnectionState previewState =
                _previewPanel.PreviewControl.GetConnectionState();

            if (_audioReconnect.Active)
            {
                try
                {
                    if (_audioReconnect.Poll(_previewPanel.PreviewControl, child, _credentialStore) is { } progress)
                        AppendLog(progress);
                }
                catch (Exception ex) when (IsDisplayableOperationFailure(ex)) { AppendLog($"音频重连失败：{ex.Message}"); }
                previewState = _previewPanel.PreviewControl.GetConnectionState();
            }

            _connectButton.Enabled =
                childSessionsEnabled && !_audioReconnect.Active &&
                previewState == RdpPreviewConnectionState.Disconnected;
            UpdateDisplaySettingsAvailability(previewState);
            _disconnectButton.Enabled =
                previewState != RdpPreviewConnectionState.Disconnected || _audioReconnect.Active;
            _logoffButton.Enabled = child is { StateQueryError: null } && child.Id != parent.Id;

            bool verifiedChildSession = false;
            string? verificationMessage = null;
            if (previewState == RdpPreviewConnectionState.Connected)
            {
                try
                {
                    SessionSnapshot verifiedChild =
                        _previewPanel.PreviewControl.VerifyConnectedChildSession();
                    string verifiedState = $"已核验目标子会话 {verifiedChild.Id}";

                    if (!string.Equals(_lastVerifiedChild, verifiedState, StringComparison.Ordinal))
                    {
                        AppendLog(verifiedState);
                        _lastVerifiedChild = verifiedState;
                    }

                    verifiedChildSession = true;
                    _lastTransientStatus = string.Empty;
                }
                catch (InvalidOperationException exception)
                {
                    // 连接协商期间子会话可能尚未报告 Active；保留状态但不把它误报为成功。
                    verificationMessage = exception.Message;
                }
            }
            else
            {
                _lastVerifiedChild = string.Empty;
                _lastTransientStatus = string.Empty;
            }

            _previewPanel.SetPreviewState(
                previewState,
                hasChildSession: child is not null,
                childSessionsEnabled: childSessionsEnabled,
                isVerifiedChildSession: verifiedChildSession,
                verificationMessage: verificationMessage);
            _muteChildAudioToggle.Enabled = !_audioReconnect.Active &&
                (previewState == RdpPreviewConnectionState.Disconnected || verifiedChildSession);
            bool shouldMuteChildAudio =
                _previewPanel.PreviewControl.AudioMode == ChildSessionAudioMode.DoNotPlay;
            if (_muteChildAudioToggle.Checked != shouldMuteChildAudio)
            {
                _muteChildAudioToggle.CheckedChanged -= ChangeChildAudioMode;
                _muteChildAudioToggle.Checked = shouldMuteChildAudio;
                _muteChildAudioToggle.CheckedChanged += ChangeChildAudioMode;
            }
            if (verificationMessage is not null)
            {
                AppendTransientStatus(_previewPanel.IsPresentingChildSignIn
                    ? (_childNeedsUnlock ? "请在预览内解锁子会话。" : "已确认子会话编号，等待 Windows 登录。登录提示可在预览内操作。")
                    : verificationMessage);
            }
            UpdateSessionSummary(
                parent,
                child,
                childSessionsEnabled,
                previewState,
                verifiedChildSession);

            string observedState =
                $"{parent.Id}|{parent.ConnectionState}|{child?.Id}|{child?.ConnectionState}|" +
                $"{childSessionsEnabled}|{previewState}|{verifiedChildSession}|{_previewPanel.IsPresentingChildSignIn}|{child?.IsLocked}|{child?.StateQueryError}";
            if (!string.Equals(_lastObservedState, observedState, StringComparison.Ordinal))
            {
                AppendLog(
                    $"父会话 {parent.Id} {parent.ConnectionState}；" +
                    $"子会话功能 {(childSessionsEnabled ? "已启用" : "未启用")}；" +
                    $"子会话 {_childSessionValue.Text}；预览 {GetPreviewStatusText(previewState, verifiedChildSession)}");
                _lastObservedState = observedState;
            }
        }
        catch (Exception exception) when (IsDisplayableOperationFailure(exception))
        {
            _statusLabel.Text = "状态读取失败";
            _statusLabel.ForeColor = ErrorColor;
            _parentSessionValue.Text = "读取失败";
            _capabilityValue.Text = "读取失败";
            _childSessionValue.Text = "状态未知";
            _previewStateValue.Text = "不可用";
            _previewPanel.SetPreviewUnavailable($"无法读取 Windows 会话状态\r\n{exception.Message}");
            _connectButton.Enabled = false;
            _muteChildAudioToggle.Enabled = false;
            _disconnectButton.Enabled = false;
            _logoffButton.Enabled = false;
            _resolutionSelector.Enabled = false;
            _customWidthSelector.Enabled = false;
            _customHeightSelector.Enabled = false;
            _scalingModeSelector.Enabled = false;
            _preserveAspectRatioToggle.Enabled = false;

            string errorState = $"状态读取失败：{exception.Message}";
            if (!string.Equals(_lastObservedState, errorState, StringComparison.Ordinal))
            {
                AppendLog(errorState);
                _lastObservedState = errorState;
            }
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    /// <summary>
    /// 更新顶栏连接标识和侧栏中的会话状态值。
    /// </summary>
    /// <param name="parent">当前父会话快照。</param>
    /// <param name="child">当前子会话快照；不存在时为 null。</param>
    /// <param name="childSessionsEnabled">Windows 子会话功能是否已启用。</param>
    /// <param name="previewState">RDP ActiveX 预览连接状态。</param>
    /// <param name="verifiedChildSession">连接目标是否已核验为活动子会话。</param>
    private void UpdateSessionSummary(
        SessionSnapshot parent,
        SessionSnapshot? child,
        bool childSessionsEnabled,
        RdpPreviewConnectionState previewState,
        bool verifiedChildSession)
    {
        _parentSessionValue.Text = $"{parent.Id} / {parent.ConnectionState}";
        _capabilityValue.Text = childSessionsEnabled ? "已启用" : "未启用";
        _capabilityValue.ForeColor = childSessionsEnabled ? AccentColor : WarningColor;
        _childSessionValue.Text = child switch
        {
            null => "未建立",
            { StateQueryError: int error } => $"编号 {child.Id.Value}，状态待确认 ({error})",
            _ => $"{child.Id} / {child.ConnectionState}",
        };

        _previewStateValue.Text = GetPreviewStatusText(previewState, verifiedChildSession);
        _previewStateValue.ForeColor = previewState switch
        {
            RdpPreviewConnectionState.Connected when verifiedChildSession => AccentColor,
            RdpPreviewConnectionState.Connecting => WarningColor,
            _ => MutedTextColor,
        };

        _statusLabel.Text = previewState switch
        {
            RdpPreviewConnectionState.Connected when verifiedChildSession => "已连接",
            RdpPreviewConnectionState.Connected when _previewPanel.IsPresentingChildSignIn && _childNeedsUnlock => "等待子会话解锁",
            RdpPreviewConnectionState.Connected when _previewPanel.IsPresentingChildSignIn => "等待子会话登录",
            RdpPreviewConnectionState.Connected => "正在核验",
            RdpPreviewConnectionState.Connecting => "正在连接",
            _ => "未连接",
        };
        _statusLabel.ForeColor = previewState switch
        {
            RdpPreviewConnectionState.Connected when verifiedChildSession => AccentColor,
            RdpPreviewConnectionState.Connecting => WarningColor,
            _ => MutedTextColor,
        };
    }

    /// <summary>
    /// 将连接枚举和会话身份核验结果映射为面向用户的简短状态文字。
    /// </summary>
    /// <param name="previewState">RDP ActiveX 预览连接状态。</param>
    /// <param name="verifiedChildSession">连接目标是否已核验为活动子会话。</param>
    /// <returns>侧栏和活动记录可复用的本地化状态文字。</returns>
    private string GetPreviewStatusText(
        RdpPreviewConnectionState previewState,
        bool verifiedChildSession)
    {
        return previewState switch
        {
            RdpPreviewConnectionState.Connected when verifiedChildSession => "已连接",
            RdpPreviewConnectionState.Connected when _previewPanel.IsPresentingChildSignIn && _childNeedsUnlock => "等待子会话解锁",
            RdpPreviewConnectionState.Connected when _previewPanel.IsPresentingChildSignIn => "等待子会话登录",
            RdpPreviewConnectionState.Connected => "待核验",
            RdpPreviewConnectionState.Connecting => "连接中",
            _ => "未连接",
        };
    }

    /// <summary>
    /// 记录正在等待子会话状态收敛的信息，避免改变已观察到的主状态去伪装成功。
    /// </summary>
    /// <param name="message">当前暂不能核验的具体原因。</param>
    private void AppendTransientStatus(string message)
    {
        string status = $"等待子会话核验：{message}";
        if (!string.Equals(_lastTransientStatus, status, StringComparison.Ordinal))
        {
            AppendLog(status);
            _lastTransientStatus = status;
        }
    }

    /// <summary>
    /// 将一条带本地时间戳的诊断记录追加到窗口日志。
    /// </summary>
    /// <param name="message">不包含密码或令牌的诊断信息。</param>
    private void AppendLog(string message)
    {
        _eventLog.Items.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");
        _eventLog.TopIndex = Math.Max(0, _eventLog.Items.Count - 1);
    }

    /// <summary>
    /// 将操作异常显示给用户，并保留可读的原生错误信息。
    /// </summary>
    /// <param name="title">错误窗口标题。</param>
    /// <param name="exception">操作失败异常。</param>
    private void ShowOperationError(string title, Exception exception)
    {
        MessageBox.Show(
            this,
            exception.Message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    /// <summary>
    /// 判断异常是否属于可以直接展示的 Windows 会话或 COM 操作失败。
    /// </summary>
    /// <param name="exception">需要分类的异常。</param>
    /// <returns>可预期的操作失败返回 true。</returns>
    private static bool IsDisplayableOperationFailure(Exception exception)
    {
        return exception is Win32Exception
            or InvalidOperationException
            or InvalidDataException
            or COMException
            or DllNotFoundException
            or EntryPointNotFoundException
            or UnauthorizedAccessException
            or ArgumentException;
    }
}
